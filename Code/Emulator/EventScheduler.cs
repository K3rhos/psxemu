namespace PSXEmu;

/// <summary>
/// Sorted-linked-list scheduler for <see cref="TimingEvent"/> instances.
///
/// Integration with CPU:
///   - <see cref="CpuPendingTicksGetter"/> reads CPU's pending-ticks counter
///     (cycles run but not yet committed to global tick counter).
///   - <see cref="CpuPendingTicksResetter"/> resets it to 0.
///   - <see cref="CpuDowncountSetter"/> sets CPU's batch-end deadline so
///     <c>Cpu.Run</c> exits at exactly the right cycle to fire next event.
///   - <see cref="CpuHasPendingInterruptGetter"/> reports whether an IRQ
///     is pending, used to force <c>downcount = 0</c> so the CPU exits
///     immediately to dispatch the IRQ.
///
/// Main loop call pattern (see Psx.RunFrame -> RunCpuTo):
///   <code>
///   while (running) {
///       Cpu.Run()                       // until pending_ticks >= downcount
///       scheduler.RunEvents()           // fire all due events, advance global tick
///       if (has_pending_irq) Cpu.DispatchInterrupt()
///   }
///   </code>
///
/// Each peripheral owns its own TimingEvent(s) scheduled directly on this scheduler.
/// See per-peripheral OnXxxEvent callbacks for the migrated dispatch entry points.
/// </summary>
public sealed class EventScheduler
{
	// Static "default" instance used by TimingEvent when no specific
	// scheduler is passed. Set by Psx constructor.
	public static EventScheduler Default { get; set; }

	// Doubly-linked sorted list of active events. Head = next-due.
	private TimingEvent _head;
	private TimingEvent _tail;
	private int _activeCount;

	/// <summary>The current event being dispatched (null outside of RunEvents).</summary>
	private TimingEvent _currentEvent;

	/// <summary>Read-only access to the in-flight event so
	/// <see cref="TimingEvent.ScheduleIfEarlier"/> can detect re-arming from
	/// inside the firing callback and force a fresh deadline (the "skip
	/// because already at or before" optimization would otherwise leave
	/// NextRunTime at the just-fired tick and the scheduler's post-callback
	/// auto-reschedule would push it to NextRunTime + Interval, which is
	/// int.MaxValue for self-managed peripherals).</summary>
	internal TimingEvent CurrentEvent => _currentEvent;

	/// <summary>
	/// Cached next-run-time of the currently-firing event. Used so that
	/// a callback can call <see cref="TimingEvent.SetInterval"/> mid-fire
	/// and we honour the new interval rather than re-using the old one.
	/// </summary>
	private long _currentEventNextRunTime;

	/// <summary>
	/// Absolute cycle counter. Only advances when events run; CPU
	/// accumulates progress in PendingTicks, which gets committed here.
	/// </summary>
	public long GlobalTickCounter { get; private set; }

	/// <summary>
	/// Snapshot of <see cref="GlobalTickCounter"/> taken at the start of
	/// <see cref="RunEvents"/>. Events fired during a single RunEvents
	/// invocation all reference the SAME event-run-tick (their
	/// <c>ticks_to_execute</c> is computed from this).
	/// </summary>
	public long EventRunTickCounter { get; private set; }

	// CPU integration hooks. Set by Psx wiring; not hard-coded on
	// MipsCore so tests can stub them.
	public System.Func<int> CpuPendingTicksGetter;
	public System.Action CpuPendingTicksResetter;
	public System.Action<int> CpuDowncountSetter;
	public System.Func<bool> CpuHasPendingInterruptGetter;

	public void Initialize() => Reset();

	public void Reset()
	{
		// Walk the active list and properly tear down every event, clear
		// linked-list pointers AND IsActive, so when peripherals re-Schedule
		// after the system reset, Schedule() correctly takes the AddActiveEvent
		// path (IsActive=false) instead of SortEvent (which would no-op on a
		// detached event and leave Cpu.Downcount unset, hanging the CPU loop).
		// Without this cleanup, a second Reset (hot-reload, user-triggered
		// restart) leaves stale Prev/Next pointers and IsActive=true.
		TimingEvent cur = _head;
		while (cur != null)
		{
			TimingEvent next = cur.Next;
			cur.Prev = null;
			cur.Next = null;
			cur.IsActive = false;
			cur = next;
		}
		_head = null;
		_tail = null;
		_activeCount = 0;
		_currentEvent = null;
		GlobalTickCounter = 0;
		EventRunTickCounter = 0;
	}

	/// <summary>Insert an event into the active list at its current NextRunTime.</summary>
	internal void AddActiveEvent(TimingEvent ev)
	{
		System.Diagnostics.Debug.Assert(ev.Prev == null && ev.Next == null);
		ev.Owner = this;
		_activeCount++;

		// If the event was scheduled relative to current time, set its
		// NextRunTime if the caller didn't already. Most callers DO set it
		// via Schedule() before calling here, so this is a safety net only.
		long now = GlobalTickCounter + (CpuPendingTicksGetter?.Invoke() ?? 0);
		if (ev.NextRunTime == 0)
			ev.NextRunTime = now + ev.Period;

		// LastRunTime tracks "time we last did work for this event". For a
		// fresh activation we use NOW so the first fire's ticks_to_execute
		// equals the cycles elapsed from Schedule -> fire (typically just the
		// requested `ticks` arg). The historical formula `NextRunTime -
		// Period` overflows long-to-int when Period is int.MaxValue (used by
		// every peripheral with self-managed scheduling, CDROM, MDEC,
		// Controller, Timers, DMA), giving a huge negative ticks_to_execute
		// on first fire. Most peripherals defensively skip work when
		// ticks <= 0, but the COMMAND was never decremented and the BIOS
		// boot polling loop would hang waiting for the IRQ that never fires.
		ev.LastRunTime = now;
		ev.IsActive = true;

		// Find insertion point: first node with NextRunTime > ours.
		TimingEvent cur = _head;
		while (cur != null && cur.NextRunTime <= ev.NextRunTime)
			cur = cur.Next;

		if (cur == null)
		{
			// Insert at tail (or as only element).
			ev.Next = null;
			ev.Prev = _tail;
			if (_tail != null) _tail.Next = ev;
			else _head = ev;
			_tail = ev;
		}
		else
		{
			// Insert before cur.
			ev.Next = cur;
			ev.Prev = cur.Prev;
			if (cur.Prev != null) cur.Prev.Next = ev;
			else _head = ev;
			cur.Prev = ev;
		}

		// New head means we need to potentially shorten the CPU's downcount.
		if (ev == _head && _currentEvent == null)
			UpdateCpuDowncount();
	}

	/// <summary>Remove an event from the active list.</summary>
	internal void RemoveActiveEvent(TimingEvent ev)
	{
		if (!ev.IsActive) return;
		_activeCount--;

		if (ev.Prev != null) ev.Prev.Next = ev.Next;
		else _head = ev.Next;
		if (ev.Next != null) ev.Next.Prev = ev.Prev;
		else _tail = ev.Prev;

		ev.Prev = null;
		ev.Next = null;
		ev.IsActive = false;

		if (_currentEvent == null && _head != null)
			UpdateCpuDowncount();
	}

	/// <summary>
	/// Re-position an event in the sorted list after its NextRunTime
	/// changed. Called after <see cref="TimingEvent.Schedule"/> updates
	/// the deadline.
	/// </summary>
	internal void SortEvent(TimingEvent ev)
	{
		if (!ev.IsActive) return;

		long runtime = ev.NextRunTime;

		// Walk backward if our deadline moved earlier than prev.
		if (ev.Prev != null && ev.Prev.NextRunTime > runtime)
		{
			TimingEvent cur = ev.Prev;
			while (cur != null && cur.NextRunTime > runtime)
				cur = cur.Prev;

			// Unlink
			if (ev.Prev != null) ev.Prev.Next = ev.Next;
			else _head = ev.Next;
			if (ev.Next != null) ev.Next.Prev = ev.Prev;
			else _tail = ev.Prev;

			// Insert after cur (or at head if cur == null)
			if (cur != null)
			{
				ev.Next = cur.Next;
				if (cur.Next != null) cur.Next.Prev = ev;
				else _tail = ev;
				ev.Prev = cur;
				cur.Next = ev;
			}
			else
			{
				_head.Prev = ev;
				ev.Prev = null;
				ev.Next = _head;
				_head = ev;
				if (_currentEvent == null) UpdateCpuDowncount();
			}
		}
		// Walk forward if our deadline moved later than next.
		else if (ev.Next != null && runtime > ev.Next.NextRunTime)
		{
			TimingEvent cur = ev.Next;
			while (cur != null && runtime > cur.NextRunTime)
				cur = cur.Next;

			// Unlink
			if (ev.Prev != null)
			{
				ev.Prev.Next = ev.Next;
			}
			else
			{
				_head = ev.Next;
				if (_currentEvent == null) UpdateCpuDowncount();
			}
			if (ev.Next != null) ev.Next.Prev = ev.Prev;
			else _tail = ev.Prev;

			// Insert before cur (or at tail if cur == null)
			if (cur != null)
			{
				ev.Next = cur;
				ev.Prev = cur.Prev;
				if (cur.Prev != null) cur.Prev.Next = ev;
				else
				{
					_head = ev;
					if (_currentEvent == null) UpdateCpuDowncount();
				}
				cur.Prev = ev;
			}
			else
			{
				_tail.Next = ev;
				ev.Next = null;
				ev.Prev = _tail;
				_tail = ev;
			}
		}
	}

	/// <summary>
	/// Set CPU's downcount so its Run loop exits when the head event
	/// becomes due. If an IRQ is already pending, force downcount=0 so
	/// the CPU exits immediately to dispatch.
	/// </summary>
	public void UpdateCpuDowncount()
	{
		if (_head == null) return;
		int eventDowncount = (int)System.Math.Max(0, _head.NextRunTime - GlobalTickCounter);
		bool hasIrq = CpuHasPendingInterruptGetter?.Invoke() ?? false;
		CpuDowncountSetter?.Invoke(hasIrq ? 0 : eventDowncount);
	}

	/// <summary>
	/// Run all events whose NextRunTime has been reached. Called by
	/// <c>Psx.RunCpuTo</c> when CPU's pending_ticks meets the downcount.
	/// </summary>
	public void RunEvents()
	{
		if (_head == null) return;

		int pendingTicks = CpuPendingTicksGetter?.Invoke() ?? 0;
		long newGlobalTicks = EventRunTickCounter + pendingTicks;

		if (newGlobalTicks >= _head.NextRunTime)
		{
			CpuPendingTicksResetter?.Invoke();
			CommitGlobalTicks(newGlobalTicks);
		}

		UpdateCpuDowncount();
	}

	/// <summary>
	/// Inner dispatch loop: advance <see cref="GlobalTickCounter"/> to
	/// <paramref name="newGlobalTicks"/>, firing each due event in order.
	/// Events that re-activate themselves (interval != 0) get re-scheduled
	/// and re-sorted automatically.
	/// </summary>
	private void CommitGlobalTicks(long newGlobalTicks)
	{
		EventRunTickCounter = newGlobalTicks;

		do
		{
			TimingEvent ev = _head;
			if (ev == null) break;
			GlobalTickCounter = System.Math.Min(newGlobalTicks, ev.NextRunTime);

			while (GlobalTickCounter >= ev.NextRunTime)
			{
				_currentEvent = ev;

				int ticksLate = (int)(GlobalTickCounter - ev.NextRunTime);
				int ticksToExecute = (int)(GlobalTickCounter - ev.LastRunTime);

				// Cache the planned next-run-time BEFORE firing the callback
				// so that callbacks calling SetInterval still get a sensible
				// auto-reschedule. Snapshot the pre-callback NextRunTime too
				// so we can detect whether the callback explicitly called
				// Schedule(), in which case we honour the callback's value
				// instead of overwriting with the auto-reschedule. Without
				// this guard, peripherals like CDROM that compute their own
				// next-deadline inside the callback (Interval=int.MaxValue)
				// would have their fresh Schedule wiped out, leaving the
				// event effectively deactivated until the next MMIO touched
				// it from outside the callback.
				long preCallbackNextRunTime = ev.NextRunTime;
				_currentEventNextRunTime = ev.NextRunTime + ev.Interval;
				ev.LastRunTime = GlobalTickCounter;

				ev.CallbackFn(ev.CallbackParam, ticksToExecute, ticksLate);

				if (ev.IsActive)
				{
					// Callback explicitly rescheduled (NextRunTime changed) -> keep
					// the callback's value; Schedule() already sorted the event.
					// Otherwise fall back to the interval-based auto-reschedule.
					if (ev.NextRunTime == preCallbackNextRunTime)
					{
						ev.NextRunTime = _currentEventNextRunTime;
						SortEvent(ev);
					}
				}

				ev = _head;
				if (ev == null) break;
			}
		} while (newGlobalTicks > GlobalTickCounter);

		_currentEvent = null;
	}

	/// <summary>
	/// Force-commit any pending CPU ticks to the global counter without
	/// requiring an event to fire. Used at frame boundaries.
	/// </summary>
	public void CommitLeftoverTicks()
	{
		int pendingTicks = CpuPendingTicksGetter?.Invoke() ?? 0;
		if (pendingTicks > 0)
		{
			CpuPendingTicksResetter?.Invoke();
			CommitGlobalTicks(EventRunTickCounter + pendingTicks);
			UpdateCpuDowncount();
		}
	}

	// ---- Save-state support ----

	/// <summary>Serialize the absolute clocks. Active events are NOT written
	/// here, each peripheral re-arms its own TimingEvent(s) on load (their
	/// callback delegates can't be serialized), restoring deadlines relative to
	/// the GlobalTickCounter we save here.</summary>
	public void SaveState(StateWriter w)
	{
		w.S64(GlobalTickCounter);
		w.S64(EventRunTickCounter);
	}

	/// <summary>Restore the absolute clocks. Runs BEFORE peripherals load,
	/// since they re-Schedule events relative to GlobalTickCounter.</summary>
	public void LoadState(StateReader r)
	{
		GlobalTickCounter = r.S64();
		EventRunTickCounter = r.S64();
	}

	/// <summary>Detach every active event (without disturbing their
	/// NextRunTime/LastRunTime) so peripherals can re-insert them via
	/// <see cref="AddRestoredEvent"/> during LoadState.</summary>
	public void ClearForLoad()
	{
		TimingEvent cur = _head;
		while (cur != null)
		{
			TimingEvent next = cur.Next;
			cur.Prev = null;
			cur.Next = null;
			cur.IsActive = false;
			cur = next;
		}
		_head = null;
		_tail = null;
		_activeCount = 0;
		_currentEvent = null;
	}

	/// <summary>Re-insert a restored event at its already-set NextRunTime
	/// (sorted), WITHOUT recomputing NextRunTime/LastRunTime the way
	/// <see cref="AddActiveEvent"/> would. Used only by save-state load.</summary>
	internal void AddRestoredEvent(TimingEvent ev)
	{
		ev.Owner = this;
		ev.IsActive = true;
		_activeCount++;
		TimingEvent cur = _head;
		while (cur != null && cur.NextRunTime <= ev.NextRunTime)
			cur = cur.Next;
		if (cur == null)
		{
			ev.Next = null;
			ev.Prev = _tail;
			if (_tail != null) _tail.Next = ev;
			else _head = ev;
			_tail = ev;
		}
		else
		{
			ev.Next = cur;
			ev.Prev = cur.Prev;
			if (cur.Prev != null) cur.Prev.Next = ev;
			else _head = ev;
			cur.Prev = ev;
		}
	}

	/// <summary>Diagnostic: dump active event list to log.</summary>
	public string DumpActiveEvents()
	{
		var sb = new System.Text.StringBuilder();
		sb.Append($"[SCHED] global={GlobalTickCounter} active_count={_activeCount} events:");
		TimingEvent cur = _head;
		while (cur != null)
		{
			sb.Append($" '{cur.Name}'@{cur.NextRunTime}(+{cur.NextRunTime - GlobalTickCounter}/{cur.Interval})");
			cur = cur.Next;
		}
		return sb.ToString();
	}
}

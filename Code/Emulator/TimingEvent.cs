namespace PSXEmu;

/// <summary>
/// A scheduled callback that fires at an exact global-tick deadline.
///
/// Peripherals create events for things they need to do at specific times,
/// "fire VBlank at cycle X", "deliver CDROM sector at cycle Y", "MDEC block
/// copy-out done at cycle Z".
///
/// The scheduler keeps active events in a sorted linked list keyed by
/// <see cref="NextRunTime"/>; the head is always the next-due event. CPU
/// downcount is set so the interpreter exits to the scheduler exactly when
/// the head event becomes due.
///
/// Lifecycle:
///   1. Create via <c>new TimingEvent(name, period, interval, callback, param)</c>.
///      Constructor does NOT auto-activate.
///   2. Call <see cref="Activate"/> to insert into the active list at
///      <c>NextRunTime = current_global_ticks + period</c>.
///   3. Callback fires when global ticks reach <see cref="NextRunTime"/>.
///      The callback receives (param, ticks_to_execute, ticks_late) so it
///      knows exactly how many ticks of work to simulate.
///   4. After callback, if still <see cref="IsActive"/>, the event is
///      re-scheduled for <c>NextRunTime + interval</c> automatically.
///   5. <see cref="Deactivate"/> removes from active list. Safe to call
///      from inside the callback; just sets a flag and the dispatcher
///      handles removal.
///
/// <see cref="InvokeEarly"/> services the accumulated time before the next
/// natural deadline, used for register reads that need fresh state (e.g.,
/// "what's the timer counter NOW", we synthesise the result from elapsed
/// ticks since the last event fire).
/// </summary>
public sealed class TimingEvent
{
	/// <summary>Callback receives (param, ticks_to_execute, ticks_late).</summary>
	public delegate void Callback(object param, int ticksToExecute, int ticksLate);

	public string Name { get; }

	/// <summary>Initial delay used by <see cref="Activate"/>.</summary>
	public int Period;

	/// <summary>Re-schedule delay after each firing.</summary>
	public int Interval;

	public Callback CallbackFn { get; }
	public object CallbackParam { get; }

	/// <summary>Absolute global tick when this event next fires.</summary>
	public long NextRunTime;

	/// <summary>Absolute global tick when this event last fired.</summary>
	public long LastRunTime;

	/// <summary>True if event is in the active list.</summary>
	public bool IsActive { get; internal set; }

	// Linked-list pointers managed by EventScheduler. Public for dispatch hot
	// path, internal would require accessor calls in tight loops.
	public TimingEvent Prev;
	public TimingEvent Next;

	// Owning scheduler reference (set on first Activate so the event knows
	// where to schedule itself). Allows tests to use multiple schedulers.
	internal EventScheduler Owner;

	public TimingEvent(string name, int period, int interval, Callback callback, object callbackParam)
	{
		Name = name;
		Period = period;
		Interval = interval;
		CallbackFn = callback;
		CallbackParam = callbackParam;
	}

	/// <summary>Add to active list at <c>NextRunTime = now + Period</c>. No-op if already active.</summary>
	public void Activate()
	{
		if (IsActive) return;
		Owner ??= EventScheduler.Default;
		Owner.AddActiveEvent(this);
	}

	/// <summary>Remove from active list. Safe inside callbacks.</summary>
	public void Deactivate()
	{
		if (!IsActive) return;
		Owner.RemoveActiveEvent(this);
	}

	/// <summary>Set Period AND re-schedule for now+ticks. Activates if inactive.</summary>
	public void SetPeriodAndSchedule(int ticks)
	{
		Period = ticks;
		Schedule(ticks);
	}

	/// <summary>Set Interval AND re-schedule for now+ticks. Activates if inactive.</summary>
	public void SetIntervalAndSchedule(int ticks)
	{
		Interval = ticks;
		Schedule(ticks);
	}

	/// <summary>
	/// Reschedule the event to fire <paramref name="ticks"/> from now.
	/// If active, re-sorts in the list. If inactive, activates and inserts.
	/// </summary>
	public void Schedule(int ticks)
	{
		Owner ??= EventScheduler.Default;
		long now = Owner.GlobalTickCounter + (Owner.CpuPendingTicksGetter?.Invoke() ?? 0);
		NextRunTime = now + ticks;

		if (IsActive)
			Owner.SortEvent(this);
		else
			Owner.AddActiveEvent(this);
	}


	/// <summary>
	/// Ensure the event fires within at most <paramref name="ticks"/> from now,
	/// but DON'T push the deadline LATER if it's already scheduled to fire
	/// sooner. Use this from "re-arming" code paths that may run many times
	/// per countdown, e.g. a peripheral's RescheduleEvent called from every
	/// MMIO write at the end of WriteByte/WriteWord. Without the
	/// "if-earlier" guard, every unrelated MMIO touch would call Schedule(N)
	/// which sets NextRunTime = NOW + N; as NOW advances with each CPU
	/// instruction the deadline keeps running away from the CPU and the event
	/// never reaches its fire time.
	/// </summary>
	public void ScheduleIfEarlier(int ticks)
	{
		Owner ??= EventScheduler.Default;
		long now = Owner.GlobalTickCounter + (Owner.CpuPendingTicksGetter?.Invoke() ?? 0);
		long newDeadline = now + ticks;

		// Special case: re-arming from INSIDE our own firing callback. At
		// this point NextRunTime equals the just-fired deadline (= now), and
		// the scheduler's post-callback auto-reschedule will overwrite it
		// with NextRunTime + Interval if we don't explicitly change it here.
		// For events with Interval = int.MaxValue (every peripheral with
		// self-managed scheduling) the auto-reschedule pushes the deadline
		// 2 billion cycles into the future, effectively deactivating the
		// event. Force a Schedule so subsequent dispatches see our new
		// deadline. Without this DMA dies in particular because nothing
		// else touches DMA MMIO between in-flight transfers.
		bool insideOwnCallback = (Owner.CurrentEvent == this);

		// Already scheduled with a deadline at-or-before the requested one ->
		// keep the existing schedule. This covers BOTH "future but sooner
		// than what we'd schedule now" AND "overdue (NextRunTime < now)",
		// in the overdue case the event is about to fire on the next
		// dispatch pass, so pushing it FORWARD to now + ticks would be a
		// regression.
		if (!insideOwnCallback && IsActive && NextRunTime <= newDeadline)
			return;

		NextRunTime = newDeadline;
		if (IsActive)
			Owner.SortEvent(this);
		else
			Owner.AddActiveEvent(this);
	}

	/// <summary>
	/// Run the event callback NOW with accumulated time since last fire.
	/// Used for MMIO reads that need fresh state, e.g., a timer counter
	/// read needs to know the value AT THE EXACT INSTRUCTION reading it,
	/// not at the last scheduled tick. Calls callback even if no ticks
	/// have elapsed when <paramref name="force"/> is true.
	/// </summary>
	public void InvokeEarly(bool force = false)
	{
		Owner ??= EventScheduler.Default;
		long now = Owner.GlobalTickCounter + (Owner.CpuPendingTicksGetter?.Invoke() ?? 0);
		long ticksSinceLast = long.Abs(now - LastRunTime);
		if (ticksSinceLast <= 0 && !force)
			return;
		LastRunTime = now;
		CallbackFn(CallbackParam, (int)ticksSinceLast, 0);
		// Re-sort: the caller may have shifted NextRunTime via SetInterval.
		if (IsActive)
			Owner.SortEvent(this);
	}

	/// <summary>Ticks since the last actual callback fire. Used by InvokeEarly-style queries.</summary>
	public int GetTicksSinceLastExecution()
	{
		Owner ??= EventScheduler.Default;
		long now = Owner.GlobalTickCounter + (Owner.CpuPendingTicksGetter?.Invoke() ?? 0);
		return (int)long.Abs(now - LastRunTime);
	}

	/// <summary>Ticks until the next scheduled fire (negative if overdue).</summary>
	public int GetTicksUntilNextExecution()
	{
		Owner ??= EventScheduler.Default;
		long now = Owner.GlobalTickCounter + (Owner.CpuPendingTicksGetter?.Invoke() ?? 0);
		return (int)long.Abs(NextRunTime - now);
	}

	// ---- Save-state support ----

	/// <summary>Serialize this event's scheduling RELATIVE to <paramref name="globalTick"/>
	/// so it survives the clock being restored to a different absolute value. The
	/// callback delegate is not serialized, the same TimingEvent instance is reused
	/// across a load, so its delegate is already intact.</summary>
	public void SaveState(StateWriter w, long globalTick)
	{
		w.Bool(IsActive);
		if (!IsActive) return;
		w.S64(NextRunTime - globalTick);
		w.S64(LastRunTime - globalTick);
		w.S32(Period);
		w.S32(Interval);
	}

	/// <summary>Restore scheduling saved by <see cref="SaveState"/> and re-insert into
	/// the scheduler's active list (cleared by <see cref="EventScheduler.ClearForLoad"/>
	/// before peripherals loaded).</summary>
	public void LoadState(StateReader r, long globalTick)
	{
		Owner ??= EventScheduler.Default;
		bool active = r.Bool();
		// Defensive detach (ClearForLoad already did this for active events).
		IsActive = false;
		Prev = null;
		Next = null;
		if (!active) return;
		NextRunTime = globalTick + r.S64();
		LastRunTime = globalTick + r.S64();
		Period = r.S32();
		Interval = r.S32();
		Owner.AddRestoredEvent(this);
	}
}

namespace PSXEmu;

public sealed partial class EmulatorComponent
{
	private void InitAudioStream()
	{
		try
		{
			_audioStream?.Dispose();
			_audioStream = new SoundStream(PsxConstants.SpuSampleRate, PsxConstants.SpuChannels);
			
			// Pre-fill silence to prevent initial audio glitches
			_audioStream.WriteData(new short[PsxConstants.MaxSpuSamplesPerFrame * PsxConstants.SpuChannels * AudioPrefillFrames]);
			
			_soundHandle = _audioStream.Play();
			_soundHandle.SpacialBlend = 0.0f;
			_soundHandle.OcclusionEnabled = false;
			_soundHandle.DistanceAttenuation = false;
			_soundHandle.AirAbsorption = false;
			_soundHandle.Transmission = false;
			_soundHandle.Stop(float.MaxValue);
		}
		catch (Exception _Exception)
		{
			PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Warn, $"Audio init failed: {_Exception.Message}");
		}
	}
	
	
	
	private void EnqueueAudioSamples(ReadOnlySpan<short> _Samples)
	{
		if (_Samples.IsEmpty || _audioRingBuffer == null)
			return;
		
		lock (_audioRingLock)
		{
			int incoming = _Samples.Length;
			
			if (incoming >= _audioRingBuffer.Length)
			{
				_Samples = _Samples.Slice(incoming - _audioRingBuffer.Length, _audioRingBuffer.Length);
				incoming = _Samples.Length;
				
				_audioRingReadPos = 0;
				_audioRingWritePos = 0;
				_audioRingCount = 0;
			}

			int overflow = Math.Max(0, _audioRingCount + incoming - _audioRingBuffer.Length);
			
			if (overflow > 0)
			{
				_audioRingReadPos = (_audioRingReadPos + overflow) % _audioRingBuffer.Length;
				_audioRingCount -= overflow;
			}

			int first = Math.Min(_Samples.Length, _audioRingBuffer.Length - _audioRingWritePos);
			
			_Samples[..first].CopyTo(_audioRingBuffer.AsSpan(_audioRingWritePos, first));
			
			int remaining = _Samples.Length - first;
			
			if (remaining > 0)
				_Samples.Slice(first, remaining).CopyTo(_audioRingBuffer.AsSpan(0, remaining));
			
			_audioRingWritePos = (_audioRingWritePos + _Samples.Length) % _audioRingBuffer.Length;
			_audioRingCount += _Samples.Length;
		}
	}
	
	
	
	private void PumpAudioStream()
	{
		if (_audioStream == null || _audioRingBuffer == null || _audioDrainBuffer == null)
			return;
		
		while (_audioStream.QueuedSampleCount < AudioTargetQueuedSamples)
		{
			int toCopy;
			
			lock (_audioRingLock)
			{
				if (_audioRingCount <= 0)
					break;

				toCopy = Math.Min(_audioDrainBuffer.Length, _audioRingCount);
				
				int first = Math.Min(toCopy, _audioRingBuffer.Length - _audioRingReadPos);
				
				_audioRingBuffer.AsSpan(_audioRingReadPos, first).CopyTo(_audioDrainBuffer.AsSpan(0, first));
				
				int remaining = toCopy - first;
				
				if (remaining > 0)
					_audioRingBuffer.AsSpan(0, remaining).CopyTo(_audioDrainBuffer.AsSpan(first, remaining));
				
				_audioRingReadPos = (_audioRingReadPos + toCopy) % _audioRingBuffer.Length;
				_audioRingCount -= toCopy;
			}

			long audioWriteStart = PsxPerfMonitor.Stamp();
			
			_audioStream.WriteData(_audioDrainBuffer.AsSpan(0, toCopy));
			
			Core?.Perf.AddTicks(PsxPerfSection.MainAudioWrite, PsxPerfMonitor.Stamp() - audioWriteStart);
			
			if (toCopy < _audioDrainBuffer.Length)
				break;
		}
	}
	
	
	
	private int GetAudioRingCount()
	{
		lock (_audioRingLock)
			return _audioRingCount;
	}
}

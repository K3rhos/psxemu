using System.Threading;

namespace PSXEmu;

public sealed partial class EmulatorComponent
{
	private const float StickDeadzone = 0.3f;
	
	
	
	private void PollInput()
	{
		if (_paused)
			return;
		
		if (_inputCooldown > 0)
		{
			_inputCooldown--;
			
			Interlocked.Exchange(ref _buttonMask, unchecked((int)0xFFFF));
			
			return;
		}
		
		int mask = unchecked((int)0xFFFF);

		if (Input.Down("Cross")) mask &= ~(int)PsxButton.Cross;
		if (Input.Down("Circle")) mask &= ~(int)PsxButton.Circle;
		if (Input.Down("Square")) mask &= ~(int)PsxButton.Square;
		if (Input.Down("Triangle")) mask &= ~(int)PsxButton.Triangle;
		if (Input.Down("L1")) mask &= ~(int)PsxButton.L1;
		if (Input.Down("R1")) mask &= ~(int)PsxButton.R1;
		if (Input.Down("L2")) mask &= ~(int)PsxButton.L2;
		if (Input.Down("R2")) mask &= ~(int)PsxButton.R2;
		if (Input.Down("Start")) mask &= ~(int)PsxButton.Start;
		if (Input.Down("Select")) mask &= ~(int)PsxButton.Select;

		float stickX = Input.GetAnalog(InputAnalog.LeftStickX);
		float stickY = Input.GetAnalog(InputAnalog.LeftStickY);
		
		if (Input.Down("Up") || stickY < -StickDeadzone) mask &= ~(int)PsxButton.Up;
		if (Input.Down("Down") || stickY > StickDeadzone) mask &= ~(int)PsxButton.Down;
		if (Input.Down("Left") || stickX < -StickDeadzone) mask &= ~(int)PsxButton.Left;
		if (Input.Down("Right") || stickX > StickDeadzone) mask &= ~(int)PsxButton.Right;

		Interlocked.Exchange(ref _buttonMask, mask);
	}
	
	
	
	private bool AnyButtonHeld()
	{
		return Input.Down("Cross") || Input.Down("Circle") ||
			Input.Down("Square") || Input.Down("Triangle") ||
			Input.Down("L1") || Input.Down("R1") ||
			Input.Down("L2") || Input.Down("R2") ||
			Input.Down("Start") || Input.Down("Select") ||
			Input.Down("Up") || Input.Down("Down") ||
			Input.Down("Left") || Input.Down("Right") ||
			MathF.Abs(Input.GetAnalog(InputAnalog.LeftStickX)) > StickDeadzone ||
			MathF.Abs(Input.GetAnalog(InputAnalog.LeftStickY)) > StickDeadzone;
	}
}

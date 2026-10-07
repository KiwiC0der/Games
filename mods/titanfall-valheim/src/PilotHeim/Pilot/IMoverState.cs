namespace PilotHeim.Pilot
{
    /// <summary>Movement state a weapon needs for spread and timing (pilot or Titan).</summary>
    public interface IMoverState
    {
        float Time { get; }
        bool OnGround { get; }
        bool Wallrunning { get; }
        bool Crouched { get; }
        bool Sprinting { get; }
    }
}

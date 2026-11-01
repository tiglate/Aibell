namespace Aibell.Audio.Midi;

/// <summary>The controller state of one MIDI channel.</summary>
internal sealed class ChannelState
{
    public int Program, BankMsb, BankLsb, Volume, Expression, Pan, Modulation, ReverbSend, ChorusSend, RpnMsb, RpnLsb;
    public float Bend, BendRange;
    public bool Sustain;

    /// <summary>The last value of every MIDI controller (read by SoundFont modulators).</summary>
    public readonly byte[] Controllers = new byte[128];

    public ChannelState() => Reset();

    public void Reset()
    {
        Program = 0;
        BankMsb = BankLsb = 0;
        Volume = 100;
        Pan = 64;
        ReverbSend = 40;
        ChorusSend = 0;
        Array.Clear(Controllers);
        Controllers[7] = 100;
        Controllers[10] = 64;
        Controllers[11] = 127;
        Controllers[91] = 40;
        ResetControllers();
    }

    /// <summary>"Reset all controllers" (CC 121): what a performer moves, not how the channel is mixed.</summary>
    public void ResetControllers()
    {
        Expression = Controllers[11] = 127;
        Modulation = Controllers[1] = 0;
        Bend = 0;
        BendRange = 2;
        Sustain = false;
        RpnMsb = RpnLsb = 127;
    }
}

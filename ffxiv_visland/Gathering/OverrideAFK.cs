using FFXIVClientStructs.FFXIV.Client.UI;

namespace visland.Gathering;

public unsafe class OverrideAFK
{
    public static void ResetTimers()
    {
        var module = UIModule.Instance()->GetInputTimerModule();
        module->AfkTimer = 0;
        module->ContentInputTimer = 0;
        module->InputTimer = 0;

        // API14 follows upstream: reset the three public input timers only.
    }
}

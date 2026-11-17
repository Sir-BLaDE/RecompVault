using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace RecompVault.Services;

public enum PadButton { Up, Down, Left, Right, A, B, X, Y, LB, RB, Start, Back }

/// <summary>
/// Mando XInput (Xbox y compatibles) sondeado 60 veces por segundo. Solo emite pulsaciones mientras
/// la ventana de RecompVault está activa, para no reaccionar mientras juegas.
/// Las direcciones (cruceta o stick izquierdo) se repiten al mantenerlas.
/// </summary>
public sealed class GamepadService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint dwPacketNumber; public XInputGamepad Gamepad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint GetState14(uint user, out XInputState state);
    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] private static extern uint GetState910(uint user, out XInputState state);

    private const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008, StartBtn = 0x0010, BackBtn = 0x0020,
        LShoulder = 0x0100, RShoulder = 0x0200, ABtn = 0x1000, BBtn = 0x2000, XBtn = 0x4000, YBtn = 0x8000;
    private const short Dead = 16000;

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isActive;
    private int _api; // 0 = sin probar, 1 = 1_4, 2 = 9_1_0, -1 = no disponible
    private readonly Dictionary<PadButton, DateTime> _held = new();
    private readonly Dictionary<PadButton, DateTime> _nextRepeat = new();
    private int _idleTicks;

    public event Action<PadButton>? Pressed;
    public bool Connected { get; private set; }

    public GamepadService(Func<bool> isActive)
    {
        _isActive = isActive;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Dispose() => _timer.Stop();

    private bool TryGet(out XInputState s)
    {
        s = default;
        try
        {
            if (_api is 0 or 1)
            {
                try { var r = GetState14(0, out s); _api = 1; return r == 0; }
                catch (DllNotFoundException) { _api = 2; }
                catch (EntryPointNotFoundException) { _api = 2; }
            }
            if (_api == 2) return GetState910(0, out s) == 0;
        }
        catch { _api = -1; }
        return false;
    }

    private void Poll()
    {
        if (_api == -1) { _timer.Stop(); return; }
        // sin mando: sondear despacio (cada ~1 s) para no gastar CPU
        if (!Connected && ++_idleTicks % 60 != 0) return;
        Connected = TryGet(out var s);
        if (!Connected) { _held.Clear(); return; }
        if (!_isActive()) { _held.Clear(); return; }

        var b = s.Gamepad.wButtons;
        var lx = s.Gamepad.sThumbLX; var ly = s.Gamepad.sThumbLY;
        var down = new HashSet<PadButton>();
        if ((b & DPadUp) != 0 || ly > Dead) down.Add(PadButton.Up);
        if ((b & DPadDown) != 0 || ly < -Dead) down.Add(PadButton.Down);
        if ((b & DPadLeft) != 0 || lx < -Dead) down.Add(PadButton.Left);
        if ((b & DPadRight) != 0 || lx > Dead) down.Add(PadButton.Right);
        if ((b & ABtn) != 0) down.Add(PadButton.A);
        if ((b & BBtn) != 0) down.Add(PadButton.B);
        if ((b & XBtn) != 0) down.Add(PadButton.X);
        if ((b & YBtn) != 0) down.Add(PadButton.Y);
        if ((b & LShoulder) != 0) down.Add(PadButton.LB);
        if ((b & RShoulder) != 0) down.Add(PadButton.RB);
        if ((b & StartBtn) != 0) down.Add(PadButton.Start);
        if ((b & BackBtn) != 0) down.Add(PadButton.Back);

        var now = DateTime.UtcNow;
        foreach (var btn in down)
        {
            if (!_held.ContainsKey(btn))
            {
                _held[btn] = now;
                _nextRepeat[btn] = now.AddMilliseconds(380);
                Pressed?.Invoke(btn);
            }
            else if (btn is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right && now >= _nextRepeat[btn])
            {
                _nextRepeat[btn] = now.AddMilliseconds(110);
                Pressed?.Invoke(btn);
            }
        }
        foreach (var released in _held.Keys.Where(k => !down.Contains(k)).ToList()) _held.Remove(released);
    }
}

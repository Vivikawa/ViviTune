namespace ViviTune.Ui;

// Повторное нажатие в течение 500 мс после принятого не принимается: двойной клик не попадает
// по соседней строке (список сдвинулся) и не переключает дважды. Время — Environment.TickCount64.
public sealed class ClickGuard
{
    public const long WindowMs = 500;
    long? last;

    public bool TryAccept(long nowMs)
    {
        if (last is long l && nowMs - l < WindowMs) return false;
        last = nowMs;
        return true;
    }
}

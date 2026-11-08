using System.Text.RegularExpressions;

namespace FseMultiMonitor;

/// <summary>
/// Clasifica procesos con pausa de render por oclusión y localiza la ventana que tapa a una fuente.
/// Solo usa datos ya capturados en <see cref="Inspector.Snapshot"/> (z-order + rectángulos + cloak).
/// </summary>
internal static partial class OcclusionAdvisor
{
    private static readonly HashSet<string> ChromiumProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "opera_gx", "discord", "electron",
        "thorium", "arc", "yandex", "browser", "chromium", "slack", "teams", "code"
    };

    private static readonly HashSet<string> GeckoProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "zen", "librewolf", "floorp", "waterfox", "palemoon", "basilisk", "thunderbird"
    };

    [GeneratedRegex(@"^\((?<x>-?\d+),(?<y>-?\d+)\)\s+(?<w>\d+)x(?<h>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RectPattern();

    internal enum BrowserFamily
    {
        None,
        Chromium,
        Gecko
    }

    internal sealed record OcclusionResult(string? OccluderProcess, int OccluderCloak, bool Occluded)
    {
        internal static readonly OcclusionResult None = new(null, -1, false);
    }

    internal static BrowserFamily FamilyOf(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return BrowserFamily.None;
        string name = Path.GetFileNameWithoutExtension(processName.Trim());
        if (GeckoProcesses.Contains(name)) return BrowserFamily.Gecko;
        if (ChromiumProcesses.Contains(name)) return BrowserFamily.Chromium;
        return BrowserFamily.None;
    }

    internal static bool IsBrowserLike(string? processName) => FamilyOf(processName) != BrowserFamily.None;

    internal static string AdviceFor(string? processName)
    {
        return FamilyOf(processName) switch
        {
            BrowserFamily.Gecko =>
                "Este navegador (Gecko) puede dejar de dibujar cuando otra ventana lo tapa.\n\n" +
                "Solución:\n" +
                "1. Abre about:config en el navegador.\n" +
                "2. Busca widget.windows.window_occlusion_tracking.enabled\n" +
                "3. Ponlo en false.\n" +
                "4. Reinicia el navegador.\n" +
                "5. Vuelve a elegir su ventana en el selector (el identificador de ventana cambia al reiniciar).\n\n" +
                "Es una optimización intencional del navegador, no un fallo de FSE Multi-Monitor.",
            BrowserFamily.Chromium =>
                "Este navegador (Chromium/Electron) puede dejar de dibujar cuando otra ventana lo tapa.\n\n" +
                "Solución:\n" +
                "1. Cierra el navegador.\n" +
                "2. Ábrelo con el argumento --disable-features=CalculateNativeWinOcclusion\n" +
                "   Ejemplo: msedge.exe --disable-features=CalculateNativeWinOcclusion\n" +
                "3. Comprueba en chrome://version (o edge://version) que el argumento aparece en la línea de comandos.\n" +
                "4. Vuelve a elegir su ventana en el selector.\n\n" +
                "Es una optimización intencional del navegador, no un fallo de FSE Multi-Monitor.",
            _ =>
                "Si la miniatura se congela solo cuando otra ventana tapa la aplicación de origen, " +
                "lo más probable es que esa aplicación haya dejado de dibujar (pausa por oclusión).\n\n" +
                "Comprobación rápida: pon al frente una ventana animada que no sea un navegador. " +
                "Si esa sigue en vivo y la tuya no, el límite está en la aplicación de origen.\n\n" +
                "Los navegadores basados en Chromium y Gecko tienen una optimización que pausa el render " +
                "cuando la ventana queda tapada. En ese caso hay que desactivarla en el navegador; " +
                "FSE Multi-Monitor no puede forzarlo desde fuera."
        };
    }

    /// <summary>
    /// Devuelve la ventana visible más alta en z-order que tapa al menos el 25 % de la fuente.
    /// Ignora ventanas cloaked (no se dibujan), la propia fuente y los procesos propios.
    /// </summary>
    internal static OcclusionResult FindOccluder(
        IReadOnlyList<Inspector.WindowState> windows,
        Inspector.WindowState source,
        int selfProcessId)
    {
        if (!TryParseRect(source.Rect, out var sourceRect) || sourceRect.Width <= 0 || sourceRect.Height <= 0)
            return OcclusionResult.None;

        double sourceArea = (double)sourceRect.Width * sourceRect.Height;
        OcclusionResult best = OcclusionResult.None;

        foreach (var window in windows)
        {
            if (window.ZOrder >= source.ZOrder) continue;
            if (window.Hwnd.Equals(source.Hwnd, StringComparison.OrdinalIgnoreCase)) continue;
            if (window.ProcessId == selfProcessId) continue;
            if (!window.Visible || window.Iconic) continue;
            if (window.Cloaked != 0) continue;
            if (!TryParseRect(window.Rect, out var candidate)) continue;

            var overlap = Rectangle.Intersect(sourceRect, candidate);
            if (overlap.IsEmpty) continue;
            double covered = (double)overlap.Width * overlap.Height / sourceArea;
            if (covered < 0.25) continue;

            // EnumWindows entrega de arriba a abajo: la primera coincidencia es la más alta.
            return new OcclusionResult(window.ProcessName, window.Cloaked, true);
        }

        return best;
    }

    private static bool TryParseRect(string value, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        var match = RectPattern().Match(value ?? string.Empty);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["x"].Value, out int x) ||
            !int.TryParse(match.Groups["y"].Value, out int y) ||
            !int.TryParse(match.Groups["w"].Value, out int w) ||
            !int.TryParse(match.Groups["h"].Value, out int h)) return false;
        rect = new Rectangle(x, y, w, h);
        return true;
    }
}

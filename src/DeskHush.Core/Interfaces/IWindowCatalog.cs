using DeskHush.Core.Models;

namespace DeskHush.Core.Interfaces;

public interface IWindowCatalog
{
    IReadOnlyList<WindowInfo> GetVisibleWindows();
}

using JmcModLib.Prefabs;
using JmcModLib.UI.PauseMenu;
using MegaCrit.Sts2.Core.Logging;

namespace SeedRestart;

public static class RestartMenu
{
    [PauseMenuButton("换种重开", Key = "seed_restart.pause_menu.rebirth", Order = 20)]
    public static async Task RestartFromPauseMenu()
    {
        Log.Info("[SeedRestart] Pause-menu restart button invoked.");
        var controller = ModEntry.EnsureController();
        if (controller == null)
        {
            Log.Error("[SeedRestart] Cannot restart because the active NGame controller is unavailable.");
            await JmcConfirmationPopup.ShowMessageAsync(
                "换种重开暂不可用", "游戏控制器尚未就绪，请关闭暂停菜单后重试；若持续出现，请保留最新日志。", "知道了", true,
                typeof(RestartMenu).Assembly);
            return;
        }
        await controller.RequestFromPauseMenuAsync();
    }
}

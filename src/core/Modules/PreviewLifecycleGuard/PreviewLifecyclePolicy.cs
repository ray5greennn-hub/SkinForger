namespace SkinForger.PreviewLifecycleGuard;

public static class PreviewLifecyclePolicy
{
    public static bool ShouldCloseForMenuState(bool menuVisible, bool previewOpen) =>
        !menuVisible && previewOpen;

    public static bool ShouldCloseForSceneLoad(bool previewOpen) => previewOpen;
}

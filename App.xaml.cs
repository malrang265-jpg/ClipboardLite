using System.Windows;

namespace ClipboardLite;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 메인 창을 표시하지 않고 트레이/단축키 프로그램으로 계속 실행한다.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;

        // 실제 화면 표시 없이 HWND와 초기화를 준비한다.
        // 따라서 시작할 때 흰 창이 잠깐 나타나는 현상을 없앤다.
        window.WarmUpWindowHandle();
        window.InitializeStartup();
    }
}

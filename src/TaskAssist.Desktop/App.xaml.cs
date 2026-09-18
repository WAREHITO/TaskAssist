using System.IO;
using System.Windows;
using TaskAssist.Core;

namespace TaskAssist.Desktop;

public partial class App : System.Windows.Application
{
    private InstanceLease? lease;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskAssist", "Live");
        try { lease = new InstanceLease(folder); }
        catch (IOException) { MessageBox.Show("仕事アシストは既に開いているか、保存先を使用できません。開いている画面を確認してください。", "起動できません"); Shutdown(2); return; }
        try
        {
            LocalSession.Start(this, new ProfileStore(folder));
        }
        catch (Exception)
        {
            MessageBox.Show("保存記録を開けませんでした。元の記録は保持しています。保存先の空き容量・アクセス権と、この版で作った記録かを確認してください。（START-01）", "起動できません");
            Shutdown(3);
        }
    }
    protected override void OnExit(ExitEventArgs e) { lease?.Dispose(); base.OnExit(e); }
}

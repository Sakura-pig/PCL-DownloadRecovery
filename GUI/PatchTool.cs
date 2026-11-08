using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PCLDownloadRecovery
{
    /// <summary>
    /// PCL 下载恢复补丁工具 —— 图形界面。
    /// 只是外面一层壳，真正打补丁的是同目录的 McRestorePatcher.exe。
    /// </summary>
    public class PatchForm : Form
    {
        private readonly TextBox _pathBox;
        private readonly Button _browse;
        private readonly Button _patch;
        private readonly Button _openDir;
        private readonly TextBox _log;
        private readonly Label _status;
        private string _input;
        private string _output;

        public PatchForm()
        {
            Text = "PCL 下载恢复补丁工具";
            ClientSize = new Size(660, 470);
            MinimumSize = new Size(660, 470);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            AllowDrop = true;

            var title = new Label();
            title.Text = "PCL 下载恢复补丁工具";
            title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(20, 16);
            Controls.Add(title);

            var tip = new Label();
            tip.Text = "把 PCL 的 exe 拖进窗口（或点“浏览…”选择），然后点“开始补丁”。\r\n" +
                       "会在原文件旁边生成 “原文件名 (dl-restore).exe”，不会改动原文件。";
            tip.AutoSize = true;
            tip.ForeColor = Color.FromArgb(90, 90, 90);
            tip.Location = new Point(22, 52);
            Controls.Add(tip);

            _pathBox = new TextBox();
            _pathBox.ReadOnly = true;
            _pathBox.Location = new Point(24, 88);
            _pathBox.Size = new Size(540, 25);
            _pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pathBox.AllowDrop = true;
            _pathBox.DragEnter += OnDragEnter;
            _pathBox.DragDrop += OnDragDrop;
            Controls.Add(_pathBox);

            _browse = new Button();
            _browse.Text = "浏览…";
            _browse.Location = new Point(572, 86);
            _browse.Size = new Size(64, 27);
            _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browse.Click += OnBrowse;
            Controls.Add(_browse);

            _patch = new Button();
            _patch.Text = "开始补丁";
            _patch.Location = new Point(24, 124);
            _patch.Size = new Size(110, 30);
            _patch.Enabled = false;
            _patch.Click += OnPatch;
            Controls.Add(_patch);

            _openDir = new Button();
            _openDir.Text = "打开输出目录";
            _openDir.Location = new Point(144, 124);
            _openDir.Size = new Size(110, 30);
            _openDir.Enabled = false;
            _openDir.Click += OnOpenDir;
            Controls.Add(_openDir);

            _status = new Label();
            _status.Text = "等待选择文件";
            _status.AutoSize = true;
            _status.ForeColor = Color.FromArgb(90, 90, 90);
            _status.Location = new Point(270, 132);
            Controls.Add(_status);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Location = new Point(24, 166);
            _log.Size = new Size(612, 284);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _log.BackColor = Color.FromArgb(250, 250, 250);
            _log.Font = new Font("Consolas", 9f);
            Controls.Add(_log);

            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            var args = Environment.GetCommandLineArgs();
            if (args.Length >= 2) SelectFile(args[1]);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            SelectFile(files[0]);
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择 PCL 的 exe";
                dialog.Filter = "PCL 启动器 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK) SelectFile(dialog.FileName);
            }
        }

        private void SelectFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            path = path.Trim().Trim('"');
            if (Directory.Exists(path)) path = Path.Combine(path, "Plain Craft Launcher 2.exe");
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "找不到文件：" + path, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "请选择 PCL 的 exe 文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _input = Path.GetFullPath(path);
            _pathBox.Text = _input;
            _patch.Enabled = true;
            _openDir.Enabled = false;
            _status.Text = "已选择文件";
            _log.Clear();
            AppendLog("已选择：" + _input);
        }

        private void OnPatch(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_input)) return;

            var dir = Path.GetDirectoryName(_input);
            var name = Path.GetFileNameWithoutExtension(_input);
            if (dir == null || name == null) return;
            _output = Path.Combine(dir, name + " (dl-restore).exe");

            if (File.Exists(_output))
            {
                var answer = MessageBox.Show(this,
                    "输出文件已存在，是否覆盖？\r\n" + _output,
                    "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            var patcher = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? "", "McRestorePatcher.exe");
            if (!File.Exists(patcher))
            {
                MessageBox.Show(this,
                    "找不到 McRestorePatcher.exe。\r\n请把本程序和 McRestorePatcher.exe、dnlib.dll 放在同一个文件夹里。",
                    "缺少文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _patch.Enabled = false;
            _browse.Enabled = false;
            _openDir.Enabled = false;
            _status.Text = "正在打补丁…";
            _log.Clear();
            AppendLog("运行：" + patcher);
            AppendLog("输入：" + _input);
            AppendLog("输出：" + _output);
            AppendLog("");

            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = patcher;
                psi.Arguments = Quote(_input) + " " + Quote(_output);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (var process = Process.Start(psi))
                {
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (stdout.Trim().Length > 0) AppendLog(stdout.TrimEnd());
                    if (stderr.Trim().Length > 0) AppendLog(stderr.TrimEnd());

                    if (process.ExitCode == 0 && File.Exists(_output))
                    {
                        _status.Text = "补丁完成";
                        _openDir.Enabled = true;
                        AppendLog("");
                        AppendLog("补丁完成。用输出文件替换原来的 PCL 即可（原文件建议先备份）。");
                        MessageBox.Show(this,
                            "补丁完成。\r\n\r\n输出文件：\r\n" + _output,
                            "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        _status.Text = "补丁失败";
                        AppendLog("");
                        AppendLog("补丁失败（退出码 " + process.ExitCode + "）。该版本可能不兼容，未写出文件。");
                        MessageBox.Show(this,
                            "补丁失败。该版本可能不兼容，详见窗口里的输出。",
                            "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                _status.Text = "补丁失败";
                AppendLog("发生错误：" + ex.Message);
                MessageBox.Show(this, "发生错误：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _patch.Enabled = true;
                _browse.Enabled = true;
            }
        }

        private void OnOpenDir(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_output) || !File.Exists(_output)) return;
            try { Process.Start("explorer.exe", "/select,\"" + _output + "\""); }
            catch { }
        }

        private void AppendLog(string text)
        {
            _log.AppendText(text + "\r\n");
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PatchForm());
        }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace SskCnInstaller;

public partial class MainForm : Form
{
    private TextBox txtGamePath = null!;
    private Button btnBrowse = null!;
    private Button btnInstall = null!;
    private RichTextBox txtLog = null!;
    private ProgressBar progressBar = null!;
    private Label lblStatus = null!;
    private CheckBox chkBackup = null!;

    public MainForm()
    {
        InitializeComponent();
        TryAutoDetectGamePath();
    }

    private void InitializeComponent()
    {
        this.Text = "Ssk汉化补丁安装器 v1.0";
        this.Size = new Size(680, 800);
        this.MinimumSize = new Size(680, 580);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
        this.Font = new Font("Microsoft YaHei UI", 9F);

        int headerHeight = 80;
        int leftMargin = 25;
        int rightMargin = 25;
        int controlWidth = this.ClientSize.Width - leftMargin - rightMargin;

        // 标题面板
        var panelHeader = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(this.ClientSize.Width, headerHeight),
            BackColor = Color.FromArgb(45, 45, 48)
        };

        var lblTitle = new Label
        {
            Text = "Sunless Skies 汉化补丁安装器",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(25, 25)
        };
        panelHeader.Controls.Add(lblTitle);

        // 从 header 下方开始布局
        int yPos = headerHeight + 20;

        // 游戏路径标签
        var lblPath = new Label
        {
            Text = "游戏路径 (选择 Sunless Skies.exe 所在文件夹):",
            Location = new Point(leftMargin, yPos),
            AutoSize = true
        };
        yPos += 50;

        // 游戏路径输入框和浏览按钮
        txtGamePath = new TextBox
        {
            Location = new Point(leftMargin, yPos),
            Width = controlWidth - 100,
            Height = 40,
            ReadOnly = true,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10F)
        };

        btnBrowse = new Button
        {
            Text = "浏览...",
            Location = new Point(leftMargin + controlWidth - 90, yPos - 1),
            Size = new Size(90, 40)
        };
        btnBrowse.Click += BtnBrowse_Click;
        yPos += 50;

        // 选项
        chkBackup = new CheckBox
        {
            Text = "安装前备份已有文件",
            Location = new Point(leftMargin, yPos),
            AutoSize = true,
            Checked = true
        };
        yPos += 40;

        // 安装按钮
        btnInstall = new Button
        {
            Text = "🚀 开始安装",
            Location = new Point(leftMargin, yPos),
            Size = new Size(controlWidth, 50),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        btnInstall.FlatAppearance.BorderSize = 0;
        btnInstall.Click += BtnInstall_Click;
        yPos += 65;

        // 进度条
        progressBar = new ProgressBar
        {
            Location = new Point(leftMargin, yPos),
            Size = new Size(controlWidth, 25),
            Style = ProgressBarStyle.Continuous
        };
        yPos += 35;

        // 状态标签
        lblStatus = new Label
        {
            Text = "请选择游戏安装目录",
            Location = new Point(leftMargin, yPos),
            Width = controlWidth,
            AutoSize = false,
            Height = 30,
            ForeColor = Color.Gray,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        yPos += 60;

        // 日志标签
        var lblLog = new Label
        {
            Text = "安装日志:",
            Location = new Point(leftMargin, yPos),
            AutoSize = true
        };
        yPos += 50;

        // 日志文本框
        txtLog = new RichTextBox
        {
            Location = new Point(leftMargin, yPos),
            Size = new Size(controlWidth, 250),
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 9F),
            BorderStyle = BorderStyle.None
        };

        // 添加控件
        this.Controls.AddRange(new Control[]
        {
            panelHeader, lblPath, txtGamePath, btnBrowse,
            chkBackup, btnInstall, progressBar, lblStatus,
            lblLog, txtLog
        });
    }

    private void TryAutoDetectGamePath()
    {
        string[] commonPaths =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Sunless Skies",
            @"C:\Program Files\Steam\steamapps\common\Sunless Skies",
            @"D:\Steam\steamapps\common\Sunless Skies",
            @"D:\SteamLibrary\steamapps\common\Sunless Skies",
            @"E:\Steam\steamapps\common\Sunless Skies",
            @"E:\SteamLibrary\steamapps\common\Sunless Skies",
            @"F:\Steam\steamapps\common\Sunless Skies",
            @"F:\SteamLibrary\steamapps\common\Sunless Skies",
        };

        foreach (var path in commonPaths)
        {
            if (ValidateGamePath(path))
            {
                txtGamePath.Text = path;
                btnInstall.Enabled = true;
                UpdateStatus($"✓ 自动检测到游戏目录", Color.Green);
                Log($"自动检测到游戏: {path}", Color.LightGreen);
                return;
            }
        }

        Log("请手动选择游戏安装目录...", Color.Yellow);
    }

    private void BtnBrowse_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择 Sunless Skies 游戏安装目录",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            if (ValidateGamePath(dialog.SelectedPath))
            {
                txtGamePath.Text = dialog.SelectedPath;
                btnInstall.Enabled = true;
                UpdateStatus("✓ 游戏目录已确认", Color.Green);
                Log($"选择的目录: {dialog.SelectedPath}", Color.LightGreen);
            }
            else
            {
                MessageBox.Show(
                    "所选目录中未找到 Sunless Skies.exe\n请确保选择了正确的游戏安装目录。",
                    "目录无效",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    private bool ValidateGamePath(string path)
    {
        if (!Directory.Exists(path)) return false;
        return File.Exists(Path.Combine(path, "Sunless Skies.exe"));
    }

    private async void BtnInstall_Click(object? sender, EventArgs e)
    {
        btnInstall.Enabled = false;
        btnBrowse.Enabled = false;
        progressBar.Value = 0;

        try
        {
            await InstallAsync(txtGamePath.Text);
            
            progressBar.Value = 100;
            UpdateStatus("✓ 安装完成!", Color.LimeGreen);
            Log("========================================", Color.Cyan);
            Log("安装完成! 请启动游戏体验汉化。", Color.LimeGreen);
            Log("========================================", Color.Cyan);

            MessageBox.Show(
                "汉化补丁安装成功!\n\n现在可以启动游戏体验中文了。\n\n首次启动需要较长时间生成缓存，请耐心等待。",
                "安装完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            UpdateStatus("✗ 安装失败", Color.Red);
            Log($"错误: {ex.Message}", Color.Red);
            MessageBox.Show($"安装失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            btnInstall.Enabled = true;
            btnBrowse.Enabled = true;
        }
    }

    private async Task InstallAsync(string gamePath)
    {
        var pluginsDir = Path.Combine(gamePath, "BepInEx", "plugins");
        var paraDir = Path.Combine(pluginsDir, "para");
        var fontsDir = Path.Combine(pluginsDir, "Fonts");

        // 步骤 1: 检查并安装 BepInEx
        UpdateStatus("检查 BepInEx...", Color.White);
        Log("检查 BepInEx 安装状态...", Color.White);
        await Task.Delay(100);

        var bepinexDir = Path.Combine(gamePath, "BepInEx");
        var bepinexCoreDir = Path.Combine(bepinexDir, "core");
        
        if (!Directory.Exists(bepinexCoreDir))
        {
            Log("未检测到 BepInEx，开始安装...", Color.Yellow);
            await InstallBepInEx(gamePath);
        }
        else
        {
            Log("✓ 已检测到 BepInEx", Color.LightGreen);
        }
        progressBar.Value = 25;

        // 步骤 2: 创建目录
        UpdateStatus("创建目录...", Color.White);
        Log("创建目标目录...", Color.White);
        
        Directory.CreateDirectory(pluginsDir);
        Directory.CreateDirectory(paraDir);
        Directory.CreateDirectory(fontsDir);
        
        Log($"  ✓ plugins 目录", Color.Gray);
        Log($"  ✓ para 目录", Color.Gray);
        Log($"  ✓ Fonts 目录", Color.Gray);
        progressBar.Value = 30;

        // 步骤 3: 备份 (如果需要)
        if (chkBackup.Checked)
        {
            UpdateStatus("备份已有文件...", Color.White);
            await BackupExistingFiles(pluginsDir);
        }
        progressBar.Value = 35;

        // 步骤 4: 释放汉化文件
        UpdateStatus("释放汉化文件...", Color.White);
        await ExtractEmbeddedResources(pluginsDir, paraDir, fontsDir);
        progressBar.Value = 95;

        // 完成
        await Task.Delay(200);
    }

    private async Task InstallBepInEx(string gamePath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var bepinexResource = "SskCnInstaller.Resources.BepInEx.BepInEx.zip";
        
        using var stream = assembly.GetManifestResourceStream(bepinexResource);
        if (stream == null)
        {
            Log("✗ 错误: 未找到内置的 BepInEx 安装包", Color.Red);
            throw new Exception("内置 BepInEx 安装包丢失，请重新下载安装程序");
        }

        Log("  正在解压 BepInEx...", Color.White);
        
        // 解压到临时目录
        var tempDir = Path.Combine(Path.GetTempPath(), $"SskCn_BepInEx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // 先保存 zip 到临时文件
            var tempZip = Path.Combine(tempDir, "BepInEx.zip");
            using (var fileStream = File.Create(tempZip))
            {
                await stream.CopyToAsync(fileStream);
            }
            
            // 解压
            ZipFile.ExtractToDirectory(tempZip, tempDir, true);
            
            // 复制文件到游戏目录
            var filesToCopy = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".zip"));
            
            int count = 0;
            foreach (var file in filesToCopy)
            {
                var relativePath = file.Substring(tempDir.Length).TrimStart('\\', '/');
                var destPath = Path.Combine(gamePath, relativePath);
                
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                
                File.Copy(file, destPath, true);
                count++;
            }
            
            Log($"  ✓ BepInEx 安装完成 ({count} 个文件)", Color.LightGreen);
        }
        finally
        {
            // 清理临时目录
            try { Directory.Delete(tempDir, true); } catch { }
        }
        
        await Task.Delay(100);
    }

    private async Task BackupExistingFiles(string pluginsDir)
    {
        var dllPath = Path.Combine(pluginsDir, "SskCnPoc.dll");
        if (File.Exists(dllPath))
        {
            var backupPath = dllPath + $".backup_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(dllPath, backupPath, true);
            Log($"已备份: SskCnPoc.dll", Color.Yellow);
        }
        await Task.Delay(50);
    }

    private async Task ExtractEmbeddedResources(string pluginsDir, string paraDir, string fontsDir)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("SskCnInstaller.Resources.") && 
                       !n.Contains(".BepInEx."))  // 排除 BepInEx（单独处理）
            .ToArray();
        
        Log($"开始释放资源 ({resourceNames.Length} 个文件)...", Color.White);
        
        int count = 0;
        foreach (var resourceName in resourceNames)
        {
            try
            {
                // 资源名格式: SskCnInstaller.Resources.xxx.yyy
                // 例如: SskCnInstaller.Resources.para.areas.json
                //       SskCnInstaller.Resources.Fonts.sourcehan
                //       SskCnInstaller.Resources.SskCnPoc.dll
                
                string targetPath;
                string displayName;

                if (resourceName.StartsWith("SskCnInstaller.Resources.para."))
                {
                    // para 目录下的 JSON 文件
                    // 格式: SskCnInstaller.Resources.para.filename.json
                    var fileName = resourceName.Substring("SskCnInstaller.Resources.para.".Length);
                    targetPath = Path.Combine(paraDir, fileName);
                    displayName = $"para/{fileName}";
                }
                else if (resourceName.StartsWith("SskCnInstaller.Resources.Fonts."))
                {
                    // Fonts 目录下的文件
                    var fileName = resourceName.Substring("SskCnInstaller.Resources.Fonts.".Length);
                    targetPath = Path.Combine(fontsDir, fileName);
                    displayName = $"Fonts/{fileName}";
                }
                else if (resourceName == "SskCnInstaller.Resources.SskCnPoc.dll")
                {
                    // 插件 DLL
                    targetPath = Path.Combine(pluginsDir, "SskCnPoc.dll");
                    displayName = "SskCnPoc.dll";
                }
                else
                {
                    // 其他文件直接放到 plugins
                    var fileName = resourceName.Substring("SskCnInstaller.Resources.".Length);
                    targetPath = Path.Combine(pluginsDir, fileName);
                    displayName = fileName;
                }

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    // 确保目标目录存在
                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    
                    using var fileStream = File.Create(targetPath);
                    await stream.CopyToAsync(fileStream);
                    
                    var size = stream.Length / 1024.0;
                    var sizeStr = size > 1024 ? $"{size/1024:F1} MB" : $"{size:F0} KB";
                    Log($"  ✓ {displayName} ({sizeStr})", Color.LightGreen);
                    count++;
                }
            }
            catch (Exception ex)
            {
                Log($"  ✗ 释放失败: {resourceName} - {ex.Message}", Color.Red);
            }
            
            progressBar.Value = 30 + (count * 60 / Math.Max(resourceNames.Length, 1));
            await Task.Delay(20);
        }
        
        Log($"共释放 {count} 个文件", Color.Cyan);
    }

    private void UpdateStatus(string text, Color color)
    {
        lblStatus.Text = text;
        lblStatus.ForeColor = color;
    }

    private void Log(string message, Color color)
    {
        txtLog.SelectionStart = txtLog.TextLength;
        txtLog.SelectionLength = 0;
        txtLog.SelectionColor = color;
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        txtLog.ScrollToCaret();
    }
}

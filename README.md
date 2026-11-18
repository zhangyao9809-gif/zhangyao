# CleanCapture — 自用截图录屏小工具（MVP v0.1）

> 工作名 CleanCapture，可以改名。改名方法：把 `CleanCapture.sln`、`src/CleanCapture/CleanCapture.csproj` 里的名字、
> `MainWindow.xaml` 的标题、`AppSettings` 不用动——真正要改的只有三处：解决方案名、工程名、窗口标题。
> 改完在 Visual Studio 里重新生成一次就行。

## 这是什么

一个只给自己用的 Windows 小工具，目标就四个字：**纯净、简洁**。

- **截图**：全屏、框选区域、选一个窗口，存成 PNG（真无损），也可以自动复制到剪贴板。
- **录屏**：全屏、框选区域、选一个窗口；开始 / 停止 / 暂停；**系统声音和麦克风都可在主窗口开关，麦克风默认关闭**。
- **画质分两档，不混着说**：
  - **高清近无损（默认）**：H.264，CRF 16。肉眼几乎看不出压缩痕迹，文件小很多，1080p 大概 30–60 MB / 分钟。
  - **真无损**：FFV1 / MKV，一帧都不丢。文件非常大，1080p 每分钟可能几百 MB，录 10 分钟就要几个 GB。录之前程序会先弹提示告诉你大概占多少。
- **没有的东西**：没有登录、没有账号、没有广告、没有捆绑安装、没有偷偷上传（无遥测）、没有开机自启。
  设置只存在程序旁边的 `CleanCapture.settings.json` 里，不写注册表，整个文件夹拷到别的电脑就能用，这就是“绿色免安装”。

## 能在什么电脑上用

- Windows 10（2004 版或更新）/ Windows 11，**64 位**。
- 需要装 Visual Studio 2022（免费的 Community 社区版就行）和 .NET 8。
- 需要 FFmpeg（见下面第 4 节），**程序里没有自带 FFmpeg**，第一次用要自己下载一次。

## 1. 文件都在哪

```
mvp-v0.1/
├── CleanCapture.sln                  # 双击这个，用 Visual Studio 打开整个项目
├── README.md                         # 你现在看的这份
├── BUILD_AND_TEST.md                 # 一步一步的搭建和测试清单
├── .gitignore                        # 告诉 Git 哪些文件不用提交
└── src/CleanCapture/
    ├── CleanCapture.csproj            # 工程文件：目标系统、要装的包都在这里
    ├── app.manifest                  # 不要求管理员权限 + 高 DPI 声明
    ├── App.xaml / App.xaml.cs        # 程序入口：读设置、建托盘
    ├── MainWindow.xaml / .cs         # 主窗口：只是个“遥控器”，点按钮、接快捷键
    ├── SettingsWindow.xaml / .cs     # 设置窗口：保存路径、画质、帧率、快捷键
    ├── RegionSelectorWindow.xaml / .cs  # 框选区域：全屏遮罩，拖框选区
    ├── Models/AppSettings.cs         # 全部设置长什么样（保存路径、画质、快捷键…）
    └── Services/
        ├── SettingsService.cs        # 设置存成 JSON、再读回来
        ├── ScreenshotService.cs      # 截图：全屏 / 区域 / 窗口，存 PNG
        ├── RecordingService.cs       # 录屏总指挥：开始 / 暂停 / 停止、混流
        ├── GdiCaptureSource.cs       # 画面来源：GDI 逐帧截图供帧（2026-11-18 修复版，见第 9 节）
        ├── SystemAudioCapture.cs     # 系统声：NAudio WASAPI 环回，只录系统声
        ├── FFmpegService.cs          # 找 ffmpeg.exe、拼命令行、起进程、算大小
        ├── HotkeyService.cs          # 全局快捷键
        ├── TrayService.cs            # 托盘图标和右键菜单
        └── WindowListHelper.cs       # 列出能录的窗口、造画面来源
```

每个模块是干什么的，文件开头都有中文注释，先看注释再看代码。

## 2. 在 Windows 上打开（第一次）

1. 把整个 `mvp-v0.1` 文件夹拷到 Windows 电脑上，比如 `D:\CleanCapture\`。
2. 双击 `CleanCapture.sln`，用 Visual Studio 2022 打开。
   - 如果弹“需要安装 .NET 8 / Windows SDK”，按提示装：.NET 8 SDK、“.NET 桌面开发”工作负载。
   - 第一次打开，Visual Studio 会自动去网上还原 NuGet 包（NAudio、System.Drawing.Common），
     需要联网一次，之后就不用了。2026-11-18 已去掉 Vortice：云端编译报 NU1101，
     原来写的 Vortice.Windows 在 NuGet 上不存在，录屏改走 GDI，不再引 Vortice 的包。
3. 顶部工具栏选 `Release`、`x64`，按 `Ctrl+Shift+B` 生成。
   - 生成成功，输出窗口最后一行会写“生成成功”。
   - 生成失败，先看 `BUILD_AND_TEST.md` 第 3 节“最可能出问题的 5 个点”，九成是那几个。

## 3. 怎么运行

- 在 Visual Studio 里按 `F5` 就能跑（调试运行）。
- 想直接双击运行：去 `src\CleanCapture\bin\Release\net8.0-windows10.0.19041.0\win-x64\`，
  双击 `CleanCapture.exe`。**记得先做第 4 节放好 ffmpeg.exe，否则录屏按钮会提示找不到 FFmpeg。**
- 主窗口右上角点 `×` 不是退出，是缩到托盘；想退出，右键托盘图标 → 退出。

## 4. FFmpeg 去哪下、放哪（只做一次）

程序不内置 FFmpeg，原因是它个头大、许可证要求分开发布。你只需要下载一次：

1. 打开 https://www.gyan.dev/ffmpeg/builds/ ，下载 `ffmpeg-release-essentials.zip`
   （选 essentials 就够了，不用 full）。
2. 解压，里面有个 `bin\ffmpeg.exe`。
3. 把 `ffmpeg.exe` **复制到 `CleanCapture.exe` 旁边**（同一个文件夹）。
4. 重启 CleanCapture，再点录屏，就不会再提示找不到 FFmpeg 了。

检查办法：打开命令行（`Win+R` → 输入 `cmd`），切到 exe 所在文件夹，输入 `ffmpeg -version`，
能打出版本号就是放对了。

> 如果你想用 `winget install Gyan.FFmpeg` 装到系统里也行，但绿色版推荐还是放 exe 旁边，
> 换电脑拷文件夹的时候 FFmpeg 也一起带走。

## 5. 怎么打包成单文件 exe（绿色版）

在 Visual Studio 的“包管理器控制台”或 Windows 终端里，切到 `mvp-v0.1` 文件夹，执行：

```powershell
dotnet publish src/CleanCapture/CleanCapture.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

完成后 `publish` 文件夹里就是单文件 `CleanCapture.exe`（外加你要自己放进去的 `ffmpeg.exe` 和设置文件）。
把整个 `publish` 文件夹拷到 U 盘 / 别的电脑，双击就能用，不用再安装。

> 单文件包第一次启动会慢一点点（要解压），是正常的。
> 如果杀毒软件弹“不认识这个程序”，点“仍要运行”或加白名单，见下面第 7 节。

## 6. 怎么用

| 想做什么 | 怎么做 |
|---|---|
| 区域截图 | 点“区域截图”，或按 `Ctrl+Alt+S`，拖框，松开就存到桌面 |
| 全屏截图 | 点“全屏截图”，或按 `Ctrl+Alt+F` |
| 窗口截图 | 点“窗口截图…”，在列表里点一个窗口 |
| 开始录屏 | 选“全屏 / 区域… / 窗口…”，点“开始录屏”，或按 `Ctrl+Alt+R` |
| 暂停 / 继续 | 点“暂停”，或按 `Ctrl+Alt+P` |
| 停止录屏 | 再点“开始录屏”那个按钮（它会变成“停止录屏”），或再按 `Ctrl+Alt+R` |
| 改设置 | 点“设置…”，改保存路径、画质、帧率、快捷键 |

- 截图默认存到桌面，文件名像 `CleanCapture-screenshot-20261118-175900.png`，不会互相覆盖。
- 录屏默认存到桌面，文件名像 `CleanCapture-recording-20261118-175900.mp4`（真无损是 `.mkv`）。
- 录屏开始前会弹一次提示，告诉你大概占多少空间，点“确定”才开始录。
- 录制中主窗口会显示“录制中 00:12”，托盘图标上也能看到时长。

### 默认快捷键（都可以在设置里改）

| 功能 | 默认键 | 为什么是这个 |
|---|---|---|
| 区域截图 | `Ctrl+Alt+S` | 避开微信 `Alt+A`、QQ `Ctrl+Alt+A` |
| 全屏截图 | `Ctrl+Alt+F` | 同上 |
| 开始 / 停止录屏 | `Ctrl+Alt+R` | 同上 |
| 暂停 / 继续 | `Ctrl+Alt+P` | 同上 |

如果改完按了没反应，一般是被别的软件占了：去设置里换个组合，比如改成 `Ctrl+Alt+Shift+S`。

## 7. 常见问题（先看这里）

1. **点录屏提示“没有找到 ffmpeg.exe”** → 第 4 节没做：把 `ffmpeg.exe` 放到 `CleanCapture.exe` 旁边，重启程序。
2. **快捷键按了没反应** → 被微信、QQ 或别的软件占了。去设置里换个组合键；也可以先退出微信/QQ 再试，确认是不是它们占的。
3. **区域截图框的位置和实际截到的不一样** → 高 DPI 问题。检查两点：`app.manifest` 里的 PerMonitorV2 还在不在；
   显示设置里两个屏幕的缩放比例是不是不一样（比如主屏 150%、副屏 100%），先在同缩放下测试。
4. **录出来的视频没声音** → 先确认两点：录的时候电脑有没有在放声音（没放声音就录不到）；右下角喇叭选的输出设备对不对。
   录屏的系统声音和麦克风都可以在主窗口开关；**麦克风默认关闭**，打开后对着麦克风说话才会录进视频。
5. **录出来的视频画面和声音对不上** → 先录 1 分钟对着口型 / 鼠标点击声检查。MVP 的做法是画面和声音分开录、最后混流，
   正常误差在 0.2 秒以内；如果差得明显，把测试结果记下来，下个版本再修同步。
6. **杀毒软件报“不认识 / 有风险”** → 自己编译的 exe 没有商业签名，杀毒软件不认识是正常的。
   点“仍要运行”或加白名单。不要去网上找来路不明的“签名工具”。
7. **录某个窗口提示“拿不到这个画面”** → 那个窗口可能关了或最小化了，拿不到它在屏幕上的位置。
   换个窗口，或改录全屏。另外注意：窗口录制是按窗口在屏幕上的位置截的，
   被别的窗口盖住的部分，录到的是盖在上面的那个窗口——要录的窗口先把它移到最前面、别被盖住。

## 8. 哪些还没做（v0.1 故意不做）

- 截图后标注（画箭头、写字、打马赛克）：v0.2 再做。
- 长截图、滚动截图、OCR 认字：不在 MVP 里。
- 摄像头画中画、麦克风：立项时定死不做，v0.1 也不做。
- 视频剪辑、直播推流：不做。
- 开机自启、账号、云同步：不做。

## 9. 重要提醒：这份代码还没在 Windows 上编译验证过

这份架子是在 Linux 虚拟机上写的，**WPF 在 Linux 上编不了，所以没有“编译通过”这回事**。
拿到 Windows 电脑上，第一件事就是按 `BUILD_AND_TEST.md` 走一遍：先生成、再按清单测。
最可能出问题的 3–5 个点已经列在 `BUILD_AND_TEST.md` 第 3 节，生成失败先看那里。

### 2026-11-18 修复：云端编译 NU1101，录屏改走 GDI（B 路线）

- **失败**：GitHub Actions 运行 44290799457，`Restore and publish` 失败 41 秒，
  `error NU1101: Unable to find package Vortice.Windows`。
- **原因**：`CleanCapture.csproj` 里写的 `Vortice.Windows 2.7.0` 在 NuGet 上不存在，
  Vortice.Windows 只是项目总名，真正的包是 Vortice.Direct3D11 之类。
  核对过（2026-11-18，NuGet 官方接口）：Vortice.Direct3D11 3.6.2 真实存在，
  且支持 net8.0-windows；NAudio 2.2.1、System.Drawing.Common 8.0.0 也都存在。
- **为什么没选换包（A 路线）**：换成 Vortice.Direct3D11 3.6.2 能过 NU1101，
  但原来 `GraphicsCaptureSource.cs` 里 WinRT 和 Vortice 之间的接口写法
  （`D3D11CreateDevice` 的重载、`Direct3D11Helper.CreateDirect3DDevice`、
  从 Surface 取 `ID3D11Texture2D`）有两三处在没有 Windows 编译的情况下核对不了，
  硬换很可能再报一次编译错。
- **改了什么**：删掉 Vortice 依赖和 `GraphicsCaptureSource.cs`，
  新增 `GdiCaptureSource.cs`，按帧率用 `CopyFromScreen` 逐帧截屏供帧，
  复用截图里已经在用的写法，不引任何新包；
  `RecordingService`、`MainWindow`、`WindowListHelper` 跟着改成用 `CaptureTarget`
  （全屏是主屏幕矩形，窗口是窗口在屏幕上的位置）。
  截图、系统声、保存路径、画质双档（FFV1 真无损 + H.264 CRF 16）都不变。
- **取舍**：CPU 会比原来高一点，60 帧 1080p 下风扇转起来是正常的，觉得卡就改回 30 帧；
  窗口录制被别的窗口盖住的部分会录到盖在上面的窗口（见第 7 节第 7 条），
  管理员权限的窗口改录全屏也能录到（GDI 截的是屏幕上的像素）。
  下一步推送后预期能通过的点：NuGet 还原只剩 NAudio 和 System.Drawing.Common，
  两个包都已核对存在，`Restore and publish` 不应再报 NU1101。

## 许可证和来源

- 自用，未发布。不要把编译好的 exe 发到网上。
- 用到的开源东西：FFmpeg（LGPL/GPL，看你下的包）、NAudio（MIT）、
  GDI 截屏（Windows 系统自带接口，不用另外装包）。
  用 FFmpeg 时请保留它自带的许可证说明文件，不要删。

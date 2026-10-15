<div align="center">

<img src="ui/Assets/ViaTTS.png" width="128" alt="ViaTTS">

# ViaTTS

游戏或语音软件内的文本转语音，接入麦克风以及播放音效，通过虚拟麦克风输出声音。

[![Release](https://img.shields.io/github/v/release/wlsnD7/ViaTTS)](https://github.com/wlsnD7/ViaTTS/releases/latest)
[![License](https://img.shields.io/github/license/wlsnD7/ViaTTS)](LICENSE)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet)

[安装](#安装) · [游戏内设置](#游戏内设置) · [快捷键](#默认快捷键) · [开发](#开发)

简体中文 | [English](README.en.md)

<table>
  <tr>
    <td><img src="docs/zh/home.png" width="400" alt="主页"></td>
    <td><img src="docs/zh/phrases.png" width="400" alt="常用语与音效"></td>
  </tr>
  <tr>
    <td><img src="docs/zh/audio.png" width="400" alt="音频"></td>
    <td><img src="docs/zh/settings.png" width="400" alt="设置"></td>
  </tr>
</table>

</div>

## 安装

- 在 [Releases](https://github.com/wlsnD7/ViaTTS/releases/latest) 页面下载安装包 `ViaTTS_版本号_setup.exe`。
- 国内用户也可以在 [Gitee](https://gitee.com/wlsnD7/ViaTTS/releases) 下载。
- 支持 Windows 10（19041 及以上）和 Windows 11。
- 使用前请阅读软件内「关于」页的常见问题和免责声明。

## 语音引擎

| 引擎 | 说明 |
|---|---|
| Edge TTS | 免费，需联网，提供多种音色。 |
| 系统语音 | Windows 自带，离线。 |
| GPT-SoVITS（可选） | 可添加任意 GPT-SoVITS 模型（GPT 权重 + SoVITS 权重 + 参考音频），版本自动识别。需要 NVIDIA 显卡和 GPT-SoVITS 整合包。 |
| Azure 语音 | 微软官方语音服务，音色比 Edge 更全，支持风格。需要 API Key，每月 50 万字符免费。 |
| Fish Audio | 可以使用社区里的大量角色音色。需要 API Key。 |
| 豆包语音 | 火山引擎的语音合成，需要 API Key。 |
| 阿里云百炼 | Qwen3-TTS 和 CosyVoice，需要 API Key。 |
| MiniMax | 支持复刻音色，需要 API Key。 |
| 自定义接口 | 连接任意返回音频的 HTTP 接口，例如本地运行的 GPT-SoVITS、IndexTTS、CosyVoice。 |

API Key 加密保存在本机，只有当前 Windows 用户能读取。联网付费引擎的常用语在第一次使用时合成，之后用缓存。

GPT-SoVITS 整合包从 [lj1995/GPT-SoVITS-windows-package](https://huggingface.co/lj1995/GPT-SoVITS-windows-package/tree/main) 下载：RTX 50 系选择 `GPT-SoVITS-v2pro-20250604-nvidia50.7z`，其他 NVIDIA 显卡选择 `GPT-SoVITS-v2pro-20250604.7z`（约 9GB）。需要使用时，在软件主页「GPT-SoVITS 目录」指定整合包位置，在「模型」里点击加号添加模型。

## 游戏内设置

1. 安装 [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)，未安装时软件会提示。
2. 在游戏或语音软件（Discord、KOOK、Oopz 等）里把**麦克风设置为 `CABLE Output`**。
3. 「音频」页默认自动输出到 `CABLE Input`，同时在耳机里监听。
4. 同时接入麦克风和文本生成的语音，需在「音频」页打开「麦克风直通」并选择你的麦克风。
5. 想让队友听到音乐或视频的声音，在「音频」页打开「应用声音」，添加对应的应用。
6. 如果游戏用「按键说话」，在「音频」页的「按键说话」里点「按键」按钮，再按下游戏里的说话键（键盘单键，或鼠标中键、侧键）。朗读期间会自动按住这个键。可在「音频」页或用快捷键随时开关。
7. 游戏请用**无边框窗口**模式，全屏下弹出输入框会让游戏最小化。

## 默认快捷键

| 功能 | 快捷键 |
|---|---|
| 打开 / 关闭输入框 | Ctrl + Alt + T |
| 聚焦输入框 | 未设置 |
| 停止播放 | Ctrl + Alt + X |
| 重复上一句 | Ctrl + Alt + R |
| 打开 / 关闭按键说话 | 未设置 |

关闭主窗口后，软件会在后台运行，右键系统托盘图标退出。

## 开发

### 结构

```
ViaTTS/
├─ ui/               界面与音频（C# / WinUI 3）
├─ gsv_server.py     GPT-SoVITS 合成服务
├─ installer/        Inno Setup 安装脚本
├─ tools/            开发脚本（翻译检查）
├─ design/           图标生成脚本
├─ docs/             README 截图
├─ start.bat         直接运行 app/ 里的程序
├─ start_debug.bat   同上，并显示 GPT-SoVITS 服务的控制台
├─ app/              dotnet publish 输出（构建后生成）
└─ dist/             安装包（构建后生成）
```

### 构建

需要 .NET 8 SDK 和 Inno Setup 6：

```
dotnet publish ui -c Release -p:Platform=x64 -o app
ISCC installer\ViaTTS.iss
```


## 许可证

- 代码以 [MIT](LICENSE) 许可证开源。
- 安装包附带的第三方组件及其许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
- 图标字样使用 [Yellowtail](https://fonts.google.com/specimen/Yellowtail)（Astigmatic，Apache License 2.0）。

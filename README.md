# ViaTTS

游戏内文字转语音：按快捷键呼出输入框，打字、回车，声音就从虚拟麦克风发出去。

## 语音引擎

| 引擎 | 说明 |
|---|---|
| Edge TTS | 免费，需联网，多种音色，每句约 2 秒。开箱即用 |
| 系统语音 | Windows 自带，离线，几乎零延迟。开箱即用 |
| GPT-SoVITS（本地） | 可添加任意 GPT-SoVITS 模型（GPT 权重 + SoVITS 权重 + 参考音频），版本自动识别，例如 [老牧师](https://github.com/cubk1/laomushi-tts)（模型需自行下载）。需要 N 卡和 GPT-SoVITS 整合包，首次加载约 30 秒，之后每句约 1 秒 |

GPT-SoVITS 是可选的：没装整合包也能用 Edge 和系统语音。整合包从 [lj1995/GPT-SoVITS-windows-package](https://huggingface.co/lj1995/GPT-SoVITS-windows-package/tree/main) 下载：RTX 50 系选 `GPT-SoVITS-v2pro-20250604-nvidia50.7z`，其他 N 卡选 `GPT-SoVITS-v2pro-20250604.7z`（约 9GB，用 7-Zip 解压）。软件里的欢迎页和主页提示会按显卡自动推荐。用它时，在主页「GPT-SoVITS 目录」指定整合包位置（或放在 ViaTTS 旁边的 `GPT-SoVITS` 文件夹，会自动找到），在「模型」里点 ＋ 添加模型。

## 安装

运行 `dist/ViaTTS_1.0.0_setup.exe`（约 23MB，自带运行时，不需要另装 .NET）。默认装到当前用户目录，不需要管理员权限。

配置、常用语和日志在 `%LOCALAPPDATA%\ViaTTS`，卸载时保留。

## 游戏内设置

1. 安装 [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)。没装时主页会提示。
2. 在游戏 / 语音软件（KOOK、Discord、YY 等）里把**麦克风选成 `CABLE Output`**。
3. 「音频」页默认自动输出到 `CABLE Input`，同时在耳机里监听。
4. 游戏用「按键说话」的，在「按键说话」里填对应按键（如 `v`、`mouse4`），朗读期间会自动按住。
5. 游戏请用**无边框窗口**模式，独占全屏下弹出输入框会让游戏最小化。

## 默认快捷键

| 功能 | 快捷键 |
|---|---|
| 打开 / 关闭输入框 | Ctrl + Alt + T |
| 聚焦输入框（输入框开着时把焦点从游戏拉回来） | 未设置 |
| 停止播放 | Ctrl + Alt + X |
| 重复上一句 | Ctrl + Alt + R |

输入框里回车朗读，这句话会留在框里显示，播完清空。再按一次快捷键收起；开启「播完后收起」时播完自动收起。

关闭主窗口只是隐藏到托盘，快捷键照常可用；右键托盘图标可以退出。

## 常见问题

**对方听不到我说话**

游戏或语音软件里的麦克风要选择CABLE Output，另外检查「音频」页的虚拟麦克风是不是输出到CABLE Input。

**快捷键没反应**

可能被其他软件占用，如果游戏以管理员身份运行，ViaTTS 也要以管理员身份运行，不然快捷键等对游戏都不起作用。

**弹出输入框时游戏最小化了**

把游戏改成无边框窗口模式。

**Edge TTS 合成失败**

Edge TTS需要联网。开着代理的话，试试关掉或者换个节点。

**GPT-SoVITS 用不了**

只支持 Nvidia显卡。第一次加载要 30 秒左右，属于正常现象。显存不够时，在「设备」里改成 CPU，不过速度会变慢。报错详情可以在「关于」页的日志里看到。

**卸载后怎么删配置**

配置、常用语和日志都在 %LOCALAPPDATA%\ViaTTS，卸载时会保留，需手动删除。

## 相比 QN-TTS 的改进

- **游戏里打字不再被打断**：QN-TTS 默认用 `Space + 数字` 这类组合，在聊天框打空格加数字就会误触发。
  ViaTTS 用系统 `RegisterHotKey`，只拦截完全相同的组合键。
- 快捷键呼出的半透明输入框，发送后焦点自动回到游戏；关掉主窗口后在托盘里继续运行。
- 常用语可绑定快捷键，也可以用自己的音频文件；文字常用语会预先生成，按下即播。
- 开头补静音，避免游戏「语音激活」吞掉第一个字；自动按住游戏的按键说话键。
- 缺少 GPT-SoVITS、模型文件或 VB-Cable 时，界面会给出提示和下载链接。

## 结构

```
ViaTTS/
├─ ui/              界面与音频（C# / WinUI 3）：Edge TTS、系统语音、播放、快捷键、配置
├─ gsv_server.py    GPT-SoVITS 合成服务（只用标准库，跑在整合包的 Python 里，用到时才启动）
├─ installer/       Inno Setup 安装脚本
├─ app/             dotnet publish 输出
└─ dist/            安装包
```

## 构建

需要 .NET 8 SDK 和 Inno Setup 6：

```
dotnet publish ui -c Release -p:Platform=x64 -o app
ISCC installer\ViaTTS.iss
```

`start_debug.bat` 会让 GPT-SoVITS 服务显示控制台窗口，方便看报错。

## 免责声明

- ViaTTS 是免费的开源软件。
- Edge TTS 用的是微软 Edge「大声朗读」的接口。用 Edge TTS 时，你输入的文字会发送到微软的服务器。
- ViaTTS 不收集、不上传任何数据，启动时会访问 GitHub 检查新版本。
- ViaTTS 会监听全局快捷键，还会模拟按下按键说话的键。个别游戏的反作弊系统可能会因此有反应，请自行判断。因此造成的封号等后果由使用者承担。
- 使用 GPT-SoVITS 模型时，不要用来冒充他人、诈骗或骚扰，由此产生的责任由使用者承担。模型的版权归原作者。

## 致谢与授权

- 代码以 [MIT](LICENSE) 许可证开源。
- 安装包附带的第三方组件及其许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
- 图标字样使用 [Yellowtail](https://fonts.google.com/specimen/Yellowtail)（Astigmatic，Apache License 2.0）。仓库里只有渲染好的图标图片，不包含字体文件；重新生成图标见 `design/make_icon_final.py`。
- 老牧师模型来自 [cubk1/laomushi-tts](https://github.com/cubk1/laomushi-tts)，语音合成基于 [GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS)。

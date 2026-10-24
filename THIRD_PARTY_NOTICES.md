# 第三方声明 / Third-Party Notices

ViaSonic 本身以 [MIT](LICENSE) 许可证开源。安装包里还包含以下第三方组件，它们各自遵循自己的许可证。

ViaSonic is licensed under [MIT](LICENSE). The installer also includes the following third-party components, each under its own license.

| 组件 / Component | 版本 / Version | 许可证 / License |
|---|---|---|
| [.NET 运行时 / .NET Runtime](https://github.com/dotnet/runtime) | 8.0 | MIT |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) | 1.7.260224002 | Microsoft 软件许可条款 / Microsoft Software License Terms |
| [WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2)（随 Windows App SDK 带入 / via Windows App SDK） | 1.0.2903.40 | BSD 3-Clause |
| [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows) | 8.2.250402 | MIT |
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | MIT |
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) | 1.13.5 | Apache 2.0 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime)（随 sherpa-onnx 带入 / via sherpa-onnx） | 1.27.1 | MIT |
| [Silero VAD](https://github.com/snakers4/silero-vad)（`Assets\silero_vad.onnx`） | — | MIT |
| [QRCoder](https://github.com/codebude/QRCoder) | 1.6.0 | MIT |
| [RVC](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI)（`rvc_server.py` 改写自 `RVCRealtimeVST/worker/rvc_worker.py` / adapted from `RVCRealtimeVST/worker/rvc_worker.py`） | 2026-07-18 | MIT |
| [Yellowtail 字体 / font](https://fonts.google.com/specimen/Yellowtail)（只用于图标图片，不包含字体文件 / icon image only, font file not included） | — | Apache 2.0 |

以下项目没有打包进 ViaSonic，由用户自行下载安装，或者只参考了协议。

The following are not bundled with ViaSonic. Users download and install them separately, or only their protocol was referenced.

- [GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS)（MIT）：可选的本地语音合成，`gsv_server.py` 运行在用户自己下载的整合包里。Optional local speech synthesis; `gsv_server.py` runs inside the package the user downloads.
- [RVC](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI)（MIT）：可选的 AI 变声，`rvc_server.py` 运行在用户自己下载的整合包里。Optional AI voice conversion; `rvc_server.py` runs inside the package the user downloads.
- [edge-tts](https://github.com/rany2/edge-tts)：Edge TTS 的通信协议和 Sec-MS-GEC 令牌算法参考了这个项目，按算法重新实现，没有使用它的代码。The Edge TTS protocol and the Sec-MS-GEC token algorithm were referenced from this project and reimplemented; none of its code is used.
- [SenseVoice](https://github.com/FunAudioLLM/SenseVoice)（[FunASR 模型许可 / FunASR Model License](https://github.com/modelscope/FunASR?tab=readme-ov-file#license)）：语音输入的本地识别模型，用户在软件里点「下载」后从 Hugging Face 下载。Local speech recognition model for voice input, downloaded from Hugging Face when the user clicks Download.
- [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)：虚拟麦克风驱动，需要用户自行安装。Virtual microphone driver, installed by the user.
- 安装程序用 [Inno Setup](https://jrsoftware.org/isinfo.php) 制作，简体中文语言文件由 Zhenghan Yang 维护。The installer is built with Inno Setup; the Simplified Chinese language file is maintained by Zhenghan Yang.

---

## .NET 运行时 / .NET Runtime

Copyright (c) .NET Foundation and Contributors

MIT License，全文见下方「MIT License」。Full text under "MIT License" below.

.NET 运行时自身包含的第三方组件见 [THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/runtime/blob/release/8.0/THIRD-PARTY-NOTICES.TXT)。Third-party components included in the .NET runtime: see the same link.

## Windows Community Toolkit

（CommunityToolkit.Common、CommunityToolkit.WinUI.Controls.SettingsControls、CommunityToolkit.WinUI.Extensions、CommunityToolkit.WinUI.Helpers、CommunityToolkit.WinUI.Triggers）

Copyright (c) .NET Foundation and Contributors

MIT License，全文见下方「MIT License」。Full text under "MIT License" below.

## NAudio

（NAudio.Core、NAudio.Wasapi）

Copyright (c) 2023 Mark Heath

MIT License，全文见下方「MIT License」。Full text under "MIT License" below.

## sherpa-onnx

（sherpa-onnx.dll、sherpa-onnx-c-api.dll）

Copyright (c) Xiaomi Corporation and the k2-fsa contributors

Licensed under the Apache License, Version 2.0. 全文见 <https://www.apache.org/licenses/LICENSE-2.0>。Full text: <https://www.apache.org/licenses/LICENSE-2.0>.

## ONNX Runtime

（onnxruntime.dll）

Copyright (c) Microsoft Corporation

MIT License，全文见下方「MIT License」。Full text under "MIT License" below.

## Silero VAD

（silero_vad.onnx）

Copyright (c) 2020-present Silero Team

MIT License，全文见下方「MIT License」。Full text under "MIT License" below.

## QRCoder

（QRCoder.dll）

Copyright (c) 2013-2018 Raffael Herrmann

MIT License，全文见下方「MIT License」。Full text under "MIT License" below.

## RVC

（`rvc_server.py`）

Copyright (c) 2023 liujing04, Copyright (c) 2023 源文雨, Copyright (c) 2023 Ftps

MIT License，全文见下方「MIT License」。Full text under "MIT License" below.

## Yellowtail

Copyright (c) 2011 by Brian J. Bonislawsky DBA Astigmatic (AOETI). All rights reserved.

Licensed under the Apache License, Version 2.0. 全文见 <https://www.apache.org/licenses/LICENSE-2.0>。Full text: <https://www.apache.org/licenses/LICENSE-2.0>.

## MIT License

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## WebView2 SDK

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors 
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Windows App SDK

ViaSonic 附带的 Windows App SDK 运行时文件遵循以下 Microsoft 软件许可条款。The Windows App SDK runtime files included with ViaSonic are licensed under the following Microsoft Software License Terms.

```
MICROSOFT SOFTWARE LICENSE TERMS
MICROSOFT WINDOWS APP SDK
________________________________________
IF YOU LIVE IN (OR ARE A BUSINESS WITH A PRINCIPAL PLACE OF BUSINESS IN) THE UNITED STATES, PLEASE READ THE “BINDING ARBITRATION AND CLASS ACTION WAIVER” SECTION BELOW. IT AFFECTS HOW DISPUTES ARE RESOLVED.
________________________________________

These license terms are an agreement between you and Microsoft Corporation (or one of its affiliates). They apply to the software named above and any Microsoft services or software updates (except to the extent such services or updates are accompanied by new or additional terms, in which case those different terms apply prospectively and do not alter your or Microsoft’s rights relating to pre-updated software or services). IF YOU COMPLY WITH THESE LICENSE TERMS, YOU HAVE THE RIGHTS BELOW.  BY USING THE SOFTWARE, YOU ACCEPT THESE TERMS.

1. INSTALLATION AND USE RIGHTS.

    a) General. Subject to the terms of this agreement, you may install and use any number of copies of the software to develop and test your applications, solely for use on Windows. When building Generative AI applications follow the guidelines in https://learn.microsoft.com/windows/ai/rai. 

    b) Included Microsoft Applications. The software may include other Microsoft applications. These license terms apply to those included applications, if any, unless other license terms are provided with the other Microsoft applications.

    c) Microsoft Platforms. The software may include components from Microsoft Windows. These components are governed by separate agreements and their own product support policies, as described in the license terms found in the installation directory for that component or in the “Licenses” folder accompanying the software.

2. DATA.

    a) Data Collection. The software may collect information about you and your use of the software, and send that to Microsoft. Microsoft may use this information to provide services and improve our products and services. You may opt-out of many of these scenarios, but not all, as described in the product documentation.  There are also some features in the software that may enable you to collect data from users of your applications. If you use these features to enable data collection in your applications, you must comply with applicable law, including providing appropriate notices to users of your applications. You can learn more about data collection and use in the help documentation and the privacy statement at https://aka.ms/privacy. Your use of the software operates as your consent to these practices.

    b) Processing of Personal Data. To the extent Microsoft is a processor or subprocessor of personal data in connection with the software, Microsoft makes the commitments in the European Union General Data Protection Regulation Terms of the Online Services Terms to all customers effective May 25, 2018, at https://docs.microsoft.com/en-us/legal/gdpr.

3. DISTRIBUTABLE CODE. The software may contain code you are permitted to distribute (i.e. make available for third parties) in applications you develop, as described in this Section.

    a) Distribution Rights. The code and test files described below are distributable if included with the software.

        i. Image Library. You may copy and distribute images, graphics, and animations in the Image Library as described in the software documentation; and

        ii. Third Party Distribution. You may permit distributors of your applications to copy and distribute any of this distributable code you elect to distribute with your applications.

    b) Distribution Requirements. For any code you distribute, you must:

        i. add significant primary functionality to it in your applications;

        ii. require distributors and external end users to agree to terms that protect it and Microsoft at least as much as this agreement; and

        iii. indemnify, defend, and hold harmless Microsoft from any claims, including attorneys’ fees, related to the distribution or use of your applications, except to the extent that any claim is based solely on the unmodified distributable code.

    c) Distribution Restrictions. You may not:

        i. use Microsoft’s trademarks or trade dress in your application in any way that suggests your application comes from or is endorsed by Microsoft; or

        ii. modify or distribute the source code of any distributable code so that any part of it becomes subject to any license that requires that the distributable code, any other part of the software, or any of Microsoft’s other intellectual property be disclosed or distributed in source code form, or that others have the right to modify it.

4. SCOPE OF LICENSE. The software is licensed, not sold. Microsoft reserves all other rights. Unless applicable law gives you more rights despite this limitation, you will not (and have no right to):

    a) work around any technical limitations in the software that only allow you to use it in certain ways;

    b) reverse engineer, decompile or disassemble the software, or otherwise attempt to derive the source code for the software, except and to the extent required by third party licensing terms governing use of certain open source components that may be included in the software;

    c) remove, minimize, block, or modify any notices of Microsoft or its suppliers in the software;

    d) use the software in any way that is against the law or to create or propagate malware; or

    e) share, publish, distribute, or lease the software (except for any distributable code, subject to the terms above), provide the software as a stand-alone offering for others to use, or transfer the software or this agreement to any third party.

5. EXPORT RESTRICTIONS. You must comply with all domestic and international export laws and regulations that apply to the software, which include restrictions on destinations, end users, and end use. For further information on export restrictions, visit https://aka.ms/exporting.

6. SUPPORT SERVICES. Microsoft is not obligated under this agreement to provide any support services for the software. Any support provided is “as is”, “with all faults”, and without warranty of any kind.

7. UPDATES. The software may periodically check for updates, and download and install them for you. You may obtain updates only from Microsoft or authorized sources. Microsoft may need to update your system to provide you with updates. You agree to receive these automatic updates without any additional notice. Updates may not include or support all existing software features, services, or peripheral devices.

8. BINDING ARBITRATION AND CLASS ACTION WAIVER. This Section applies if you live in (or, if a business, your principal place of business is in) the United States.  If you and Microsoft have a dispute, you and Microsoft agree to try for 60 days to resolve it informally. If you and Microsoft can’t, you and Microsoft agree to binding individual arbitration before the American Arbitration Association under the Federal Arbitration Act (“FAA”), and not to sue in court in front of a judge or jury. Instead, a neutral arbitrator will decide. Class action lawsuits, class-wide arbitrations, private attorney-general actions, and any other proceeding where someone acts in a representative capacity are not allowed; nor is combining individual proceedings without the consent of all parties. The complete Arbitration Agreement contains more terms and is at https://aka.ms/arb-agreement-4. You and Microsoft agree to these terms.

9. ENTIRE AGREEMENT. This agreement, and any other terms Microsoft may provide for supplements, updates, or third-party applications, is the entire agreement for the software.

10. APPLICABLE LAW AND PLACE TO RESOLVE DISPUTES. If you acquired the software in the United States or Canada, the laws of the state or province where you live (or, if a business, where your principal place of business is located) govern the interpretation of this agreement, claims for its breach, and all other claims (including consumer protection, unfair competition, and tort claims), regardless of conflict of laws principles, except that the FAA governs everything related to arbitration. If you acquired the software in any other country, its laws apply, except that the FAA governs everything related to arbitration. If U.S. federal jurisdiction exists, you and Microsoft consent to exclusive jurisdiction and venue in the federal court in King County, Washington for all disputes heard in court (excluding arbitration). If not, you and Microsoft consent to exclusive jurisdiction and venue in the Superior Court of King County, Washington for all disputes heard in court (excluding arbitration).

11. CONSUMER RIGHTS; REGIONAL VARIATIONS. This agreement describes certain legal rights. You may have other rights, including consumer rights, under the laws of your state or country. Separate and apart from your relationship with Microsoft, you may also have rights with respect to the party from which you acquired the software. This agreement does not change those other rights if the laws of your state or country do not permit it to do so. For example, if you acquired the software in one of the below regions, or mandatory country law applies, then the following provisions apply to you:

    a) Australia. You have statutory guarantees under the Australian Consumer Law and nothing in this agreement is intended to affect those rights.

    b) Canada. If you acquired this software in Canada, you may stop receiving updates by turning off the automatic update feature, disconnecting your device from the Internet (if and when you re-connect to the Internet, however, the software will resume checking for and installing updates), or uninstalling the software. The product documentation, if any, may also specify how to turn off updates for your specific device or software.

    c) Germany and Austria.

        i. Warranty. The properly licensed software will perform substantially as described in any Microsoft materials that accompany the software. However, Microsoft gives no contractual guarantee in relation to the licensed software.

        ii. Limitation of Liability. In case of intentional conduct, gross negligence, claims based on the Product Liability Act, as well as, in case of death or personal or physical injury, Microsoft is liable according to the statutory law.

        Subject to the foregoing clause ii., Microsoft will only be liable for slight negligence if Microsoft is in breach of such material contractual obligations, the fulfillment of which facilitate the due performance of this agreement, the breach of which would endanger the purpose of this agreement and the compliance with which a party may constantly trust in (so-called "cardinal obligations"). In other cases of slight negligence, Microsoft will not be liable for slight negligence.

12. DISCLAIMER OF WARRANTY. THE SOFTWARE IS LICENSED “AS IS.” YOU BEAR THE RISK OF USING IT. MICROSOFT GIVES NO EXPRESS WARRANTIES, GUARANTEES, OR CONDITIONS. TO THE EXTENT PERMITTED UNDER APPLICABLE LAWS, MICROSOFT EXCLUDES ALL IMPLIED WARRANTIES, INCLUDING MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, AND NON-INFRINGEMENT.

13. LIMITATION ON AND EXCLUSION OF DAMAGES. IF YOU HAVE ANY BASIS FOR RECOVERING DAMAGES DESPITE THE PRECEDING DISCLAIMER OF WARRANTY, YOU CAN RECOVER FROM MICROSOFT AND ITS SUPPLIERS ONLY DIRECT DAMAGES UP TO U.S. $5.00. YOU CANNOT RECOVER ANY OTHER DAMAGES, INCLUDING CONSEQUENTIAL, LOST PROFITS, SPECIAL, INDIRECT, OR INCIDENTAL DAMAGES.

This limitation applies to (a) anything related to the software, services, content (including code) on third party Internet sites, or third party applications; and (b) claims for breach of contract, warranty, guarantee, or condition; strict liability, negligence, or other tort; or any other claim; in each case to the extent permitted by applicable law.

It also applies even if Microsoft knew or should have known about the possibility of the damages. The above limitation or exclusion may not apply to you because your state, province, or country may not allow the exclusion or limitation of incidental, consequential, or other damages.
```

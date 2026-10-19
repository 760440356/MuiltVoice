# MuiltVoice

Windows 桌面虚拟音频路由混音应用：把多个输入设备（麦克风、系统回放 Loopback、其他虚拟线路）混音后，同时输出到多个目标设备（耳机、音箱、虚拟麦克风）。

借助 [VB-Cable](https://vb-audio.com/Cable/) 虚拟声卡，可将混音结果暴露为系统虚拟麦克风，供腾讯会议 / Teams / OBS 等应用通过 "CABLE Output" 录音设备读取。

## 技术栈

- .NET 9 + WPF
- [NAudio 2.2.1](https://github.com/naudio/NAudio)（MIT）— WASAPI 捕获/渲染、MixingSampleProvider、WdlResampler
- VB-Cable（第三方虚拟声卡，单线缆免费可商用）

## 架构

```
N 路 WasapiCapture → 每路 BufferedWaveProvider → WdlResampler(48k stereo float)
   → MixingSampleProvider(求和) → TeeSampleProvider(扇出)
        ├─ 主输出 WasapiOut (CABLE Input / 物理设备，驱动整条 pull 链)
        ├─ BufferedWaveProvider → WasapiOut (耳机)
        └─ BufferedWaveProvider → WasapiOut (音箱)
```

关键模式（基于 NAudio 官方推荐）：
- 每路输入独立 `BufferedWaveProvider`（`ReadFully=true` + `DiscardOnBufferOverflow=true`），避免多 capture 回调线程数据竞争并吸收时钟漂移
- 多输出采用 Tee 扇出：主输出驱动 pull 链，副本广播到次级输出的独立缓冲
- 统一格式 48kHz stereo IEEE float 便于线性叠加

## 项目结构

| 项目 | 说明 |
|---|---|
| `src/MuiltVoice.Audio` | 音频核心（设备枚举、混音、扇出、引擎）|
| `src/MuiltVoice.Config` | JSON 配置存档（无 NAudio 依赖）|
| `src/MuiltVoice.App` | WPF 主程序 |
| `tests/MuiltVoice.Audio.Demo` | 控制台 demo（设备枚举 / 单路 / 多路 / 多输出）|
| `tests/MuiltVoice.Audio.Tests` | xUnit 单元测试 |

## 构建

```powershell
dotnet build MuiltVoice.sln -c Release
dotnet test MuiltVoice.sln        # 24 个单元测试
```

## 运行

### 开发调试
```powershell
dotnet run --project src/MuiltVoice.App
```

### 控制台 demo（验证音频链路）
```powershell
dotnet run --project tests/MuiltVoice.Audio.Demo
# 菜单：1=列出设备 2=单路直通 3=多路混音 4=多输入多输出
```

### 发布为单文件可执行（免 .NET 安装）
```powershell
dotnet publish src/MuiltVoice.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
# 产物：src/MuiltVoice.App/bin/Release/net9.0-windows10.0.19041.0/win-x64/publish/MuiltVoice.exe
```

## 使用流程

1. （可选但推荐）从 https://vb-audio.com/Cable/ 安装 VB-Cable，用于暴露虚拟麦克风
2. 启动 MuiltVoice
3. 左栏勾选输入设备（麦克风、系统回放 Loopback 等），可调音量/静音
4. 右栏勾选输出设备（耳机、CABLE Input 等）；第一个选中输出作为主输出
5. 点 **▶ 开始**；电平表显示各路峰值
6. （可选）保存为方案，下次加载即可恢复配置
7. 想让其他 App 拿到混音：把 **CABLE Input** 选为输出，目标 App 的录音设备选 **CABLE Output**

## 设备热插拔

后台轮询（2s）检测设备增删，自动刷新设备列表并保留选中/音量/静音状态。运行中拔插设备会提示停止后重新开始。

## 配置存档

方案 JSON 存于 `%AppData%\MuiltVoice\profiles\<name>.json`，包含每路输入/输出的设备 Id、选中状态、音量、静音。

## 当前限制 / 后续

- 路由模型为广播：所有选中输入都送入所有选中输出（无 per-output 输入子集矩阵）
- 无自动异常重连（单路 capture/render 故障需手动停止后重启）
- VB-Cable 需用户自行安装；程序检测到缺失时在 UI 提示并给出安装链接
- 商用若需多路虚拟麦克风，需自行付费加装多对 VB-Cable

## 授权说明 License

本项目采用 [PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0) 协议。

**允许（免费）：**
- 个人学习、研究、娱乐、业余项目等非商业用途
- 慈善、教育、公共科研、政府机构等非商业组织使用
- 修改、分发本软件（须附带本协议副本，且不得用于商业收费）

**禁止：**
- 任何商业用途，包括但不限于：产品开发/制造/销售、有偿服务、以创收或降本为目的的使用
- 未经授权将本软件（或其修改版/衍生作品）用于商业产品或服务

**商用授权：**
如需商业使用，请联系作者获取单独的商业授权。

完整协议文本见仓库根目录 [LICENSE](./LICENSE) 文件。

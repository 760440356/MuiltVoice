// Copyright (c) 2026 wdd. Licensed under PolyForm Noncommercial 1.0.0.
using MuiltVoice.Audio.Devices;
using MuiltVoice.Audio.Pipeline;
using NAudio.CoreAudioApi;
using System.Text;

namespace MuiltVoice.Audio.Demo;

/// <summary>
/// MuiltVoice 控制台 demo（P0 + P1）。
/// 用法：dotnet run --project tests/MuiltVoice.Audio.Demo
/// 菜单：1=列出设备；2=单路直通（麦克风/Loopback → 指定输出）。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "MuiltVoice Demo";

        using var enumerator = new AudioDeviceEnumerator();
        using var engine = new AudioEngine();

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("====== MuiltVoice Demo ======");
            Console.WriteLine($"  1. 列出设备并识别 CABLE");
            Console.WriteLine($"  2. 单路直通 (P1)");
            Console.WriteLine($"  3. 多路混音 (P2)");
            Console.WriteLine($"  4. 多输入多输出路由 (P3)");
            Console.WriteLine($"  0. 退出");
            Console.Write("选择: ");
            var key = ReadLineSafe();
            switch (key)
            {
                case "1":
                    ListDevices(enumerator);
                    break;
                case "2":
                    RunPassthrough(enumerator, engine);
                    break;
                case "3":
                    RunMixing(enumerator, engine);
                    break;
                case "4":
                    RunMultiOutput(enumerator, engine);
                    break;
                case "0":
                    return 0;
            }
        }
    }

    private static void ListDevices(AudioDeviceEnumerator enumerator)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        var render = enumerator.ListRenderDevices();
        var capture = enumerator.ListCaptureDevices();
        var defaultRender = enumerator.GetDefaultRenderDevice();
        var defaultCapture = enumerator.GetDefaultCaptureDevice();

        PrintSection("输出 / 播放设备 (Render)", render, defaultRender?.Id);
        Console.WriteLine();
        PrintSection("输入 / 录音设备 (Capture)", capture, defaultCapture?.Id);

        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        var cableIn = render.FirstOrDefault(d => d.IsCableInput);
        var cableOut = capture.FirstOrDefault(d => d.IsCableOutput);
        Console.WriteLine("VB-Cable 检测:");
        Console.WriteLine($"  CABLE Input  (Render): {Status(cableIn)}");
        Console.WriteLine($"  CABLE Output (Capture): {Status(cableOut)}");
        if (cableIn is null || cableOut is null)
        {
            Console.WriteLine("  未检测到 VB-Cable，可使用其他虚拟音频设备（如 ToDesk/网易虚拟音频设备）测试。");
        }
    }

    private static void RunPassthrough(AudioDeviceEnumerator enumerator, AudioEngine engine)
    {
        var capture = enumerator.ListCaptureDevices();
        if (capture.Count == 0)
        {
            Console.WriteLine("无可用输入设备。");
            return;
        }
        var render = enumerator.ListRenderDevices();
        if (render.Count == 0)
        {
            Console.WriteLine("无可用输出设备。");
            return;
        }

        // 选择输入
        Console.WriteLine();
        Console.WriteLine("--- 输入设备 ---");
        for (int i = 0; i < capture.Count; i++)
        {
            var d = capture[i];
            Console.WriteLine($"  {i + 1}. [{(d.IsDefault ? "*" : " ")}] {d.FriendlyName}");
        }
        Console.WriteLine($"  {capture.Count + 1}. 系统 Loopback (默认输出设备的回放)");
        Console.Write($"选择输入 (1-{capture.Count + 1}): ");
        if (!int.TryParse(ReadLineSafe(), out int inIdx) || inIdx < 1 || inIdx > capture.Count + 1)
        {
            Console.WriteLine("无效选择。");
            return;
        }
        bool loopback = inIdx == capture.Count + 1;
        var inputDevice = loopback ? null : capture[inIdx - 1];

        // 选择输出
        Console.WriteLine();
        Console.WriteLine("--- 输出设备 ---");
        for (int i = 0; i < render.Count; i++)
        {
            var d = render[i];
            var tag = d.IsCableInput ? " [CABLE-IN]" : d.IsDefault ? " [default]" : "";
            Console.WriteLine($"  {i + 1}. {d.FriendlyName}{tag}");
        }
        Console.Write($"选择输出 (1-{render.Count}): ");
        if (!int.TryParse(ReadLineSafe(), out int outIdx) || outIdx < 1 || outIdx > render.Count)
        {
            Console.WriteLine("无效选择。");
            return;
        }
        var outputDevice = render[outIdx - 1];

        // 启动
        try
        {
            if (loopback)
            {
                // Loopback：用默认 render 设备做 loopback 源
                var def = enumerator.GetDefaultRenderDevice()
                    ?? throw new InvalidOperationException("无默认输出设备，无法 Loopback。");
                engine.StartPassthrough(enumerator, def.Id, loopback: true, outputDevice.Id);
            }
            else
            {
                engine.StartPassthrough(enumerator, inputDevice!.Id, loopback: false, outputDevice.Id);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动失败: {ex.Message}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"已启动: [{(loopback ? "Loopback" : inputDevice!.FriendlyName)}] -> [{outputDevice.FriendlyName}]");
        Console.WriteLine("对着麦克风说话（或播放音频）应能听到输出。按回车停止...");
        _ = ReadLineSafe();

        engine.Stop();
        Console.WriteLine("已停止。");
    }

    private static void RunMixing(AudioDeviceEnumerator enumerator, AudioEngine engine)
    {
        var capture = enumerator.ListCaptureDevices();
        var render = enumerator.ListRenderDevices();
        if (capture.Count == 0 || render.Count == 0)
        {
            Console.WriteLine("输入或输出设备不足。");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("--- 输入设备（可多选，用逗号分隔，如 1,2）---");
        for (int i = 0; i < capture.Count; i++)
        {
            var d = capture[i];
            Console.WriteLine($"  {i + 1}. [{(d.IsDefault ? "*" : " ")}] {d.FriendlyName}");
        }
        Console.WriteLine($"  {capture.Count + 1}. 系统 Loopback (默认输出回放)");
        Console.Write($"选择输入: ");
        var inputStr = ReadLineSafe() ?? string.Empty;
        var specs = new List<InputSpec>();
        foreach (var token in inputStr.Split(',', ';'))
        {
            if (!int.TryParse(token.Trim(), out int idx) || idx < 1 || idx > capture.Count + 1)
            {
                continue;
            }
            if (idx == capture.Count + 1)
            {
                var def = enumerator.GetDefaultRenderDevice();
                if (def is null)
                {
                    Console.WriteLine("无默认输出设备，跳过 Loopback。");
                    continue;
                }
                specs.Add(new InputSpec { DeviceId = def.Id, Loopback = true, DisplayName = "Loopback" });
            }
            else
            {
                specs.Add(new InputSpec { DeviceId = capture[idx - 1].Id, Loopback = false, DisplayName = capture[idx - 1].FriendlyName });
            }
        }
        if (specs.Count == 0)
        {
            Console.WriteLine("未选择有效输入。");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("--- 输出设备 ---");
        for (int i = 0; i < render.Count; i++)
        {
            var d = render[i];
            var tag = d.IsCableInput ? " [CABLE-IN]" : d.IsDefault ? " [default]" : "";
            Console.WriteLine($"  {i + 1}. {d.FriendlyName}{tag}");
        }
        Console.Write($"选择输出 (1-{render.Count}): ");
        if (!int.TryParse(ReadLineSafe(), out int outIdx) || outIdx < 1 || outIdx > render.Count)
        {
            Console.WriteLine("无效选择。");
            return;
        }
        var outputDevice = render[outIdx - 1];

        try
        {
            engine.StartMixing(enumerator, specs, outputDevice.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动失败: {ex.Message}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"已启动混音 ({specs.Count} 路) -> [{outputDevice.FriendlyName}]");
        Console.WriteLine("实时电平显示中，按回车停止...");
        _ = Task.Run(() =>
        {
            _ = ReadLineSafe();
            _stopFlag = true;
        });

        _stopFlag = false;
        while (!_stopFlag && engine.IsRunning)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < engine.Inputs.Count; i++)
            {
                var ch = engine.Inputs[i];
                var name = specs[i].DisplayName;
                if (string.IsNullOrEmpty(name)) name = ch.Name;
                sb.Append($"  [{name}] peak={ch.PeakLevel:F3} vol={ch.Volume:F1}{(ch.IsMuted ? " [M]" : "")}");
            }
            Console.Write("\r" + sb.ToString().PadRight(Console.WindowWidth - 1));
            Thread.Sleep(100);
        }
        Console.WriteLine();

        engine.Stop();
        Console.WriteLine("已停止。");
    }

    private static void RunMultiOutput(AudioDeviceEnumerator enumerator, AudioEngine engine)
    {
        var capture = enumerator.ListCaptureDevices();
        var render = enumerator.ListRenderDevices();
        if (capture.Count == 0 || render.Count == 0)
        {
            Console.WriteLine("输入或输出设备不足。");
            return;
        }

        // 选择输入（可多选）
        Console.WriteLine();
        Console.WriteLine("--- 输入设备（可多选，逗号分隔，如 1,2）---");
        for (int i = 0; i < capture.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. [{(capture[i].IsDefault ? "*" : " ")}] {capture[i].FriendlyName}");
        }
        Console.WriteLine($"  {capture.Count + 1}. 系统 Loopback (默认输出回放)");
        Console.Write("选择输入: ");
        var inputSpecs = ParseInputs(enumerator, capture, ReadLineSafe() ?? string.Empty);
        if (inputSpecs.Count == 0)
        {
            Console.WriteLine("未选择有效输入。");
            return;
        }

        // 选择输出（可多选，第一个为主输出）
        Console.WriteLine();
        Console.WriteLine("--- 输出设备（可多选，第一个为主输出）---");
        for (int i = 0; i < render.Count; i++)
        {
            var d = render[i];
            var tag = d.IsCableInput ? " [CABLE-IN]" : d.IsDefault ? " [default]" : "";
            Console.WriteLine($"  {i + 1}. {d.FriendlyName}{tag}");
        }
        Console.Write("选择输出（逗号分隔）: ");
        var outputSpecs = new List<OutputSpec>();
        var tokens = (ReadLineSafe() ?? string.Empty).Split(',', ';');
        bool first = true;
        foreach (var token in tokens)
        {
            if (!int.TryParse(token.Trim(), out int idx) || idx < 1 || idx > render.Count)
            {
                continue;
            }
            var d = render[idx - 1];
            outputSpecs.Add(new OutputSpec
            {
                DeviceId = d.Id,
                IsMaster = first,
                DisplayName = d.FriendlyName,
            });
            first = false;
        }
        if (outputSpecs.Count == 0)
        {
            Console.WriteLine("未选择有效输出。");
            return;
        }

        try
        {
            engine.StartWithRouting(enumerator, inputSpecs, outputSpecs);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动失败: {ex.Message}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"已启动：{inputSpecs.Count} 路输入 → {outputSpecs.Count} 路输出（主: {outputSpecs[0].DisplayName}）");
        Console.WriteLine("实时电平显示中，按回车停止...");
        _ = Task.Run(() =>
        {
            _ = ReadLineSafe();
            _stopFlag = true;
        });

        _stopFlag = false;
        while (!_stopFlag && engine.IsRunning)
        {
            var sb = new StringBuilder();
            foreach (var ch in engine.Inputs)
            {
                sb.Append($"  [in {ch.Name}] peak={ch.PeakLevel:F3}");
            }
            for (int i = 0; i < engine.Outputs.Count; i++)
            {
                var ch = engine.Outputs[i];
                sb.Append($"  [out {ch.Name}{(ch.IsMaster ? "*" : "")}] buf={ch.BufferedBytes}");
            }
            Console.Write("\r" + sb.ToString().PadRight(Console.WindowWidth - 1));
            Thread.Sleep(100);
        }
        Console.WriteLine();

        engine.Stop();
        Console.WriteLine("已停止。");
    }

    private static bool _stopFlag;

    /// <summary>解析用户输入的逗号分隔索引为 InputSpec 列表。</summary>
    private static List<InputSpec> ParseInputs(
        AudioDeviceEnumerator enumerator, IReadOnlyList<DeviceInfo> capture, string input)
    {
        var specs = new List<InputSpec>();
        foreach (var token in input.Split(',', ';'))
        {
            if (!int.TryParse(token.Trim(), out int idx) || idx < 1 || idx > capture.Count + 1)
            {
                continue;
            }
            if (idx == capture.Count + 1)
            {
                var def = enumerator.GetDefaultRenderDevice();
                if (def is null)
                {
                    Console.WriteLine("无默认输出设备，跳过 Loopback。");
                    continue;
                }
                specs.Add(new InputSpec { DeviceId = def.Id, Loopback = true, DisplayName = "Loopback" });
            }
            else
            {
                specs.Add(new InputSpec
                {
                    DeviceId = capture[idx - 1].Id,
                    Loopback = false,
                    DisplayName = capture[idx - 1].FriendlyName,
                });
            }
        }
        return specs;
    }

    private static void PrintSection(string title, IReadOnlyList<DeviceInfo> devices, string? defaultId)
    {
        Console.WriteLine(title + $"  [共 {devices.Count} 个]");
        Console.WriteLine($"  {"#",-3} {"默认",-4} {"名称"}");
        Console.WriteLine($"  {new string('-', 54)}");
        if (devices.Count == 0)
        {
            Console.WriteLine("  (无)");
            return;
        }
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var def = d.Id == defaultId ? "[*]" : "   ";
            var name = d.FriendlyName;
            if (d.IsVirtualCable)
            {
                name = "⚡ " + name;
            }
            Console.WriteLine($"  {i + 1,-3} {def} {name}");
        }
    }

    private static string Status(DeviceInfo? d)
        => d is null ? "未检测到 ✗" : $"已检测 ✓  ({d.FriendlyName})";

    private static string? ReadLineSafe()
    {
        try
        {
            return Console.ReadLine();
        }
        catch
        {
            return null;
        }
    }
}

using System.Diagnostics;
using Yita.Core.Settings;

namespace Yita.Native.Mac;

/// <summary>
/// Stores the DeepSeek key in the logged-in user's macOS Keychain.
/// The `security` helper receives the value through stdin so the key is not
/// placed in the process argument list. The helper is never started off macOS.
/// </summary>
public sealed class MacKeychainSecretStore : ISecretStore
{
    private const string Service = "com.yita.Yita";
    private const string Account = "deepseek-api-key";

    public async Task<string?> ReadApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var result = await RunSecurityAsync(
            new[] { "find-generic-password", "-a", Account, "-s", Service, "-w" },
            null,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public async Task SaveApiKeyAsync(string value, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS Keychain is unavailable.");

        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            var deleted = await RunSecurityAsync(
                new[] { "delete-generic-password", "-a", Account, "-s", Service },
                null,
                cancellationToken).ConfigureAwait(false);
            // `security` returns a non-zero status when the item is absent;
            // deleting an already absent key is an idempotent operation.
            if (deleted.ExitCode != 0 && deleted.ExitCode != 44)
                throw new InvalidOperationException("无法删除 macOS Keychain 凭据。");
            return;
        }

        var saved = await RunSecurityAsync(
            new[] { "add-generic-password", "-U", "-a", Account, "-s", Service, "-w" },
            normalized,
            cancellationToken).ConfigureAwait(false);
        if (saved.ExitCode != 0)
            throw new InvalidOperationException("无法保存 macOS Keychain 凭据。");
    }

    private static async Task<SecurityResult> RunSecurityAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 macOS Keychain 工具。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("无法启动 macOS Keychain 工具。", exception);
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await errorTask.ConfigureAwait(false);
        return new SecurityResult(process.ExitCode, await outputTask.ConfigureAwait(false));
    }

    private readonly record struct SecurityResult(int ExitCode, string StandardOutput);
}

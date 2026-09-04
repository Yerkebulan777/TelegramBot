using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramBot.Core.Config;
using TelegramBot.Core.Models;
using TelegramBot.Data;

namespace TelegramBot.RootPathSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new RootPathSetupForm(CreateRootPathDataService()));
    }

    private static RootPathDataService CreateRootPathDataService()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Server")))
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        return new RootPathDataService(configuration, NullLogger<RootPathDataService>.Instance);
    }
}

internal sealed class RootPathSetupForm : Form
{
    private readonly RootPathDataService _rootPathDataService;
    private readonly TextBox _pathTextBox = new() { Dock = DockStyle.Fill };
    private readonly Label _statusLabel = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly Button _prepareButton = new() { Text = "Подготовить изменение", AutoSize = true };

    public RootPathSetupForm(RootPathDataService rootPathDataService)
    {
        _rootPathDataService = rootPathDataService;
        Text = "TelegramBot — Изменить рабочую папку";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 210);

        var browseButton = new Button { Text = "Выбрать папку…", AutoSize = true };
        browseButton.Click += SelectFolder;
        _prepareButton.Click += PrepareChange;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 5,
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Выберите сетевой диск или папку. После подготовки подтвердите изменение в /help администратором бота.",
            AutoSize = true,
            MaximumSize = new Size(510, 0)
        }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        layout.Controls.Add(_pathTextBox, 0, 1);
        layout.Controls.Add(browseButton, 1, 1);
        layout.Controls.Add(_prepareButton, 0, 2);
        layout.SetColumnSpan(_prepareButton, 2);
        layout.Controls.Add(_statusLabel, 0, 3);
        layout.SetColumnSpan(_statusLabel, 2);
        Controls.Add(layout);
        AcceptButton = _prepareButton;
    }

    private void SelectFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Выберите рабочую сетевую папку" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _pathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void PrepareChange(object? sender, EventArgs e)
    {
        _ = PrepareChangeAsync();
    }

    private async Task PrepareChangeAsync()
    {
        var displayPath = _pathTextBox.Text.Trim();
        if (!new UncRootPathValidator().TryValidate(displayPath, out var uncPath, out var error))
        {
            _statusLabel.Text = $"⚠️ {error}";
            return;
        }

        _prepareButton.Enabled = false;
        _statusLabel.Text = "Подготовка заявки…";
        try
        {
            var change = new PendingRootPathChange(
                Guid.NewGuid(), uncPath, DateTimeOffset.UtcNow, PendingRootPathChange.PendingStatus);
            var saved = await _rootPathDataService.CreatePendingRootPathChangeAsync(change);
            _statusLabel.Text = saved
                ? "Заявка подготовлена. В течение 30 минут откройте /help в Telegram и подтвердите изменение."
                : "⚠️ Не удалось сохранить заявку. Проверьте подключение к базе данных.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"⚠️ Не удалось подготовить заявку: {ex.Message}";
        }
        finally
        {
            _prepareButton.Enabled = true;
        }
    }
}

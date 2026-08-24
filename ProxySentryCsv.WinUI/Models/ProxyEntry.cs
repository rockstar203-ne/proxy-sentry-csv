using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace ProxySentryCsv.Models;

public sealed class ProxyEntry : INotifyPropertyChanged
{
    private bool _used;
    private string _status = "Ready";
    private int? _pingMs;
    private int? _responseMs;
    private string _speedGroup = "Unchecked";
    private bool _isOpen;
    private bool _copied;
    private bool _isFavorite;
    private bool _syncSelected;
    private bool _useForEventSentry;
    private string _displayName = "";
    private string _browserProvider = "chrome";
    private string _countryCode = "";
    private string _region = "";
    private string _city = "";
    private string _email = "";
    private string _eventSentryState = "Closed";
    private string _eventSentryRunId = "";
    private DateTimeOffset? _eventSentryHeartbeat;
    private int _listNumber;

    public int RowNumber { get; set; }
    public int ListNumber
    {
        get => _listNumber;
        set
        {
            if (_listNumber == value)
            {
                return;
            }

            _listNumber = value;
            OnPropertyChanged();
        }
    }

    public string Scheme { get; set; } = "http";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string RawProxy { get; set; } = "";
    public string OpenedProvider { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public bool BrowserProviderFromCsv { get; set; }

    public string BrowserProvider
    {
        get => _browserProvider;
        set
        {
            var normalized = NormalizeBrowserProvider(value);
            if (_browserProvider == normalized)
            {
                return;
            }

            _browserProvider = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BrowserDisplayName));
        }
    }

    public string BrowserDisplayName => BrowserProvider switch
    {
        "chrome" => "Chrome",
        "edge" => "Edge",
        "firefox" => "Firefox",
        _ => "Jancy"
    };

    public string NormalizedProxy => $"{Scheme.ToLowerInvariant()}://{Username}:{Password}@{Host.ToLowerInvariant()}:{Port}";

    public string StableId
    {
        get
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizedProxy));
            return Convert.ToHexString(bytes);
        }
    }

    public string ProfileKey => StableId[..16].ToLowerInvariant();

    public string ProxySummary
    {
        get
        {
            var auth = string.IsNullOrWhiteSpace(Username) ? "" : " auth";
            return $"{Scheme.ToLowerInvariant()}://{Host}:{Port}{auth}";
        }
    }

    public string ProxyDisplay => string.IsNullOrWhiteSpace(Host) ? "-" : Host;

    public string StandardProxy => string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password)
        ? $"{Host}:{Port}"
        : $"{Host}:{Port}:{Username}:{Password}";

    public string CountryCode
    {
        get => _countryCode;
        set
        {
            var next = (value ?? "").Trim().ToUpperInvariant();
            if (_countryCode == next)
            {
                return;
            }

            _countryCode = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RegionText));
        }
    }

    public string Region
    {
        get => _region;
        set
        {
            var next = (value ?? "").Trim();
            if (_region == next)
            {
                return;
            }

            _region = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RegionText));
        }
    }

    public string City
    {
        get => _city;
        set
        {
            var next = (value ?? "").Trim();
            if (_city == next)
            {
                return;
            }

            _city = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RegionText));
        }
    }

    public string RegionText
    {
        get
        {
            var region = !string.IsNullOrWhiteSpace(Region) ? Region : City;
            if (!string.IsNullOrWhiteSpace(CountryCode) && !string.IsNullOrWhiteSpace(region))
            {
                return $"{CountryCode} / {region}";
            }

            return !string.IsNullOrWhiteSpace(region)
                ? region
                : !string.IsNullOrWhiteSpace(CountryCode) ? CountryCode : "-";
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value)
            {
                return;
            }

            _displayName = value;
            OnPropertyChanged();
        }
    }

    public string Email
    {
        get => _email;
        set
        {
            var next = (value ?? "").Trim();
            if (_email == next)
            {
                return;
            }

            _email = next;
            OnPropertyChanged();
        }
    }

    public bool Used
    {
        get => _used;
        set
        {
            if (_used == value)
            {
                return;
            }

            _used = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsedText));
            OnPropertyChanged(nameof(RowBackground));
            OnPropertyChanged(nameof(UsedBadgeBrush));
        }
    }

    public bool Copied
    {
        get => _copied;
        set
        {
            if (_copied == value)
            {
                return;
            }

            _copied = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CopyButtonText));
            OnPropertyChanged(nameof(CopyButtonBrush));
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteButtonText));
            OnPropertyChanged(nameof(FavoriteButtonBrush));
        }
    }

    public bool SyncSelected
    {
        get => _syncSelected;
        set
        {
            if (_syncSelected == value)
            {
                return;
            }

            _syncSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SyncSelectText));
            OnPropertyChanged(nameof(SyncCheckOpacity));
            OnPropertyChanged(nameof(SyncSelectButtonBrush));
            OnPropertyChanged(nameof(SyncSelectBorderBrush));
            OnPropertyChanged(nameof(RowBackground));
        }
    }

    public bool UseForEventSentry
    {
        get => _useForEventSentry;
        set
        {
            if (_useForEventSentry == value)
            {
                return;
            }

            _useForEventSentry = value;
            OnPropertyChanged();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
            {
                return;
            }

            _isOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenButtonText));
            OnPropertyChanged(nameof(OpenButtonBrush));
            OnPropertyChanged(nameof(OpenButtonForegroundBrush));
        }
    }

    public string OpenButtonText => IsOpen ? "Close" : "Open";

    public SolidColorBrush OpenButtonBrush => IsOpen
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 220, 53, 69))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 13, 110, 253));

    public SolidColorBrush OpenButtonForegroundBrush => new(Colors.White);

    public string CopyButtonText => Copied ? "Copied" : "Copy";

    public SolidColorBrush CopyButtonBrush => Copied
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 25, 135, 84))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 73, 80, 87));

    public string FavoriteButtonText => IsFavorite ? "Unfav" : "Fav";

    public SolidColorBrush FavoriteButtonBrush => IsFavorite
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 193, 7))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 73, 80, 87));

    public string SyncSelectText => SyncSelected ? "✓" : "";

    public double SyncCheckOpacity => SyncSelected ? 1.0 : 0.0;

    public SolidColorBrush SyncSelectButtonBrush => SyncSelected
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 13, 110, 253))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 38, 42, 47));

    public SolidColorBrush SyncSelectBorderBrush => SyncSelected
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 91, 157, 255))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 139, 148, 158));

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public string EventSentryState
    {
        get => _eventSentryState;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "Closed" : value.Trim();
            if (_eventSentryState == next)
            {
                return;
            }

            _eventSentryState = next;
            OnPropertyChanged();
        }
    }

    public string EventSentryRunId
    {
        get => _eventSentryRunId;
        set
        {
            var next = (value ?? "").Trim();
            if (_eventSentryRunId == next)
            {
                return;
            }

            _eventSentryRunId = next;
            OnPropertyChanged();
        }
    }

    public DateTimeOffset? EventSentryHeartbeat
    {
        get => _eventSentryHeartbeat;
        set
        {
            if (_eventSentryHeartbeat == value)
            {
                return;
            }

            _eventSentryHeartbeat = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EventSentryHeartbeatText));
        }
    }

    public string EventSentryHeartbeatText => EventSentryHeartbeat.HasValue
        ? EventSentryHeartbeat.Value.ToLocalTime().ToString("HH:mm:ss")
        : "-";

    public int? PingMs
    {
        get => _pingMs;
        set
        {
            if (_pingMs == value)
            {
                return;
            }

            _pingMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PingText));
        }
    }

    public int? ResponseMs
    {
        get => _responseMs;
        set
        {
            if (_responseMs == value)
            {
                return;
            }

            _responseMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResponseText));
        }
    }

    public string SpeedGroup
    {
        get => _speedGroup;
        set
        {
            if (_speedGroup == value)
            {
                return;
            }

            _speedGroup = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedBadgeBrush));
            OnPropertyChanged(nameof(SpeedRank));
        }
    }

    public string UsedText => Used ? "Used" : "New";

    public string PingText => PingMs.HasValue ? $"{PingMs.Value} ms" : "-";

    public string ResponseText => ResponseMs.HasValue ? $"{ResponseMs.Value} ms" : "-";

    public int SpeedRank => SpeedGroup switch
    {
        "Fast" => 0,
        "Good" => 1,
        "Slow" => 2,
        "Dead" => 3,
        _ => 4
    };

    public SolidColorBrush RowBackground => SyncSelected
        ? new SolidColorBrush(ColorHelper.FromArgb(92, 13, 110, 253))
        : Used
            ? new SolidColorBrush(ColorHelper.FromArgb(38, 40, 167, 69))
            : new SolidColorBrush(Colors.Transparent);

    public SolidColorBrush UsedBadgeBrush => Used
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 40, 167, 69))
        : new SolidColorBrush(ColorHelper.FromArgb(255, 108, 117, 125));

    public SolidColorBrush SpeedBadgeBrush => SpeedGroup switch
    {
        "Fast" => new SolidColorBrush(ColorHelper.FromArgb(255, 25, 135, 84)),
        "Good" => new SolidColorBrush(ColorHelper.FromArgb(255, 13, 110, 253)),
        "Slow" => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 193, 7)),
        "Dead" => new SolidColorBrush(ColorHelper.FromArgb(255, 220, 53, 69)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 108, 117, 125))
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string NormalizeBrowserProvider(string? provider)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            "portable-chromium" or "portable chromium" or "chromium" or "portable" => "chrome",
            "chrome" => "chrome",
            "edge" => "edge",
            "firefox" => "firefox",
            _ => "jancy"
        };
    }
}

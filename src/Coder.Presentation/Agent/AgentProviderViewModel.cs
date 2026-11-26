using System.Windows.Input;
using Coder.Core.Ai;
using Coder.Presentation.Mvvm;
using Coder.Presentation.Settings;

namespace Coder.Presentation.Agent;

/// <summary>
/// A provider in Coder Agent's list: its name and address (edited in place), its key (kept encrypted,
/// never shown again) and its models, fetched on demand and remembered until something changes.
/// </summary>
public sealed class AgentProviderViewModel : ObservableObject
{
    private readonly IApiKeyStore _keys;
    private readonly Func<AiProviderConfig, string?, IChatProvider> _create;
    private string _name;
    private string _baseUrl;
    private IReadOnlyList<ModelInfo>? _models;
    private string? _error;
    private bool _isLoading;
    private Task<IReadOnlyList<ModelInfo>>? _loading;
    private string _keyInput = string.Empty;

    public AgentProviderViewModel(AiProviderConfig config, IApiKeyStore keys, Func<AiProviderConfig, string?, IChatProvider> create, Action<AgentProviderViewModel> remove)
    {
        Id = config.Id;
        Kind = config.Kind;
        _name = config.Name;
        _baseUrl = config.BaseUrl;
        _keys = keys;
        _create = create;
        SaveKeyCommand = new RelayCommand(SaveKey);
        ClearKeyCommand = new RelayCommand(() => SetKey(null));
        TestCommand = new RelayCommand(() => _ = RefreshAsync());
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Id { get; }

    public AiProviderKind Kind { get; }

    public string KindName => AiProviders.KindName(Kind);

    public string KeyHint => AiProviders.KeyHint(Kind);

    public bool NeedsKey => AiProviders.NeedsKey(Kind);

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value))
            {
                Forget();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised when the name or address changes (the settings to save changed).</summary>
    public event EventHandler? Changed;

    public AiProviderConfig Config => new(Id, Kind, string.IsNullOrWhiteSpace(Name) ? KindName : Name.Trim(), AiProviders.NormalizeUrl(BaseUrl));

    public bool HasKey => !string.IsNullOrEmpty(_keys.Get(Id));

    /// <summary>A key being typed; stored (encrypted) by <see cref="SaveKeyCommand"/>, then cleared.</summary>
    public string KeyInput
    {
        get => _keyInput;
        set => SetProperty(ref _keyInput, value ?? string.Empty);
    }

    /// <summary>Usable now: has a key, or needs none.</summary>
    public bool IsReady => HasKey || !NeedsKey;

    public IReadOnlyList<ModelInfo>? Models
    {
        get => _models;
        private set
        {
            if (SetProperty(ref _models, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Why the models could not be listed, if they couldn't.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>One line under the provider: what to do next, or how it stands.</summary>
    public string StatusText =>
        IsLoading ? "Connecting…"
        : Error is { } error ? error
        : !IsReady ? "Add an API key to use this provider."
        : Models is { } models ? models.Count == 0 ? "Connected, but it offers no models." : $"Connected · {models.Count} model{(models.Count == 1 ? string.Empty : "s")}"
        : HasKey ? "Key saved (encrypted for your Windows account)." : "Ready.";

    public bool HasError => Error is not null;

    public ICommand SaveKeyCommand { get; }

    public ICommand ClearKeyCommand { get; }

    public ICommand TestCommand { get; }

    public ICommand RemoveCommand { get; }

    public IChatProvider CreateClient() => _create(Config, _keys.Get(Id));

    public AgentProviderSetting ToSetting() => new()
    {
        Id = Id,
        Kind = KindId(Kind),
        Name = Name,
        BaseUrl = BaseUrl,
    };

    public void SetKey(string? key)
    {
        _keys.Set(Id, key);
        Forget();
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(StatusText));
    }

    private void SaveKey()
    {
        if (KeyInput.Trim().Length == 0)
        {
            return;
        }

        SetKey(KeyInput);
        KeyInput = string.Empty;
        _ = RefreshAsync();
    }

    /// <summary>The models, from the provider the first time and remembered after; empty (with <see cref="Error"/>) when it fails.</summary>
    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync() =>
        Models is { } models ? Task.FromResult(models)
        : !IsReady ? Task.FromResult<IReadOnlyList<ModelInfo>>([])
        : _loading ??= LoadAsync();

    public Task<IReadOnlyList<ModelInfo>> RefreshAsync()
    {
        Forget();
        return GetModelsAsync();
    }

    private async Task<IReadOnlyList<ModelInfo>> LoadAsync()
    {
        await Task.Yield(); // so _loading is set before this can finish
        IsLoading = true;
        Error = null;
        try
        {
            var models = await CreateClient().ListModelsAsync(CancellationToken.None);
            Models = models;
            return models;
        }
        catch (AiProviderException exception)
        {
            Error = exception.Message;
            return [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"{Config.Name}: {exception.Message}";
            return [];
        }
        finally
        {
            IsLoading = false;
            _loading = null;
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>What a model can do, when the provider said.</summary>
    public ModelInfo? FindModel(string id) => Models?.FirstOrDefault(m => m.Id == id);

    private void Forget()
    {
        _loading = null;
        Models = null;
        Error = null;
        OnPropertyChanged(nameof(HasError));
    }

    public static string KindId(AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenRouter => "openrouter",
        AiProviderKind.FreeLlmApi => "freellmapi",
        AiProviderKind.Ollama => "ollama",
        _ => "openai",
    };

    public static AiProviderKind ParseKind(string? id) => id?.ToLowerInvariant() switch
    {
        "openrouter" => AiProviderKind.OpenRouter,
        "freellmapi" => AiProviderKind.FreeLlmApi,
        "ollama" => AiProviderKind.Ollama,
        _ => AiProviderKind.OpenAiCompatible,
    };
}

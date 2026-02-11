using Playnite.SDK;
using Playnite.SDK.Data;
using System.Collections.Generic;

namespace IsthereanydealCollectionSyncModified
{
    public class Settings : ObservableObject
    {
        private ImportMode importMode = ImportMode.Skip;
        public ImportMode ImportMode 
        { 
            get => importMode;
            set => SetValue(ref importMode, value);
        }

        private bool removeFromWaitlist = true;
        public bool RemoveFromWaitlist
        {
            get => removeFromWaitlist;
            set => SetValue(ref removeFromWaitlist, value);
        }

        private string[] tags;
        public string[] Tags
        {
            get => tags;
            set => SetValue(ref tags, value);
        }

        private bool syncNote = false;
        public bool SyncNote
        {
            get => syncNote;
            set => SetValue(ref syncNote, value);
        }

        private bool skipNoSource = false;
        public bool SkipNoSource
        {
            get => skipNoSource;
            set => SetValue(ref skipNoSource, value);
        }

        private bool syncDuplicateHider = true;
        public bool SyncDuplicateHider
        {
            get => syncDuplicateHider;
            set => SetValue(ref syncDuplicateHider, value);
        }

        private bool redeemEpic = false;
        public bool RedeemEpic
        {
            get => redeemEpic;
            set => SetValue(ref redeemEpic, value);
        }

        private bool syncHidden = false;
        public bool SyncHidden
        {
            get => syncHidden;
            set => SetValue(ref syncHidden, value);
        }

        private bool filterFaileds = true;
        public bool FilterFaileds
        {
            get => filterFaileds;
            set => SetValue(ref filterFaileds, value);
        }

        private bool autoRunOnLibraryUpdate = true;
        public bool AutoRunOnLibraryUpdate
        {
            get => autoRunOnLibraryUpdate;
            set => SetValue(ref autoRunOnLibraryUpdate, value);
        }
    }

    public enum ImportMode
    {
        Skip,
        Replace,
    }

    public class IsthereanydealCollectionSyncSettingsViewModel : ObservableObject, ISettings
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IsthereanydealCollectionSync plugin;
        private Settings editing;

        public Settings Settings
        {
            get => editing;
            set => SetValue(ref editing, value);
        }

        public IsthereanydealCollectionSyncSettingsViewModel(IsthereanydealCollectionSync plugin)
        {
            this.plugin = plugin;
            editing = Serialization.GetClone(plugin.client.Settings);
            plugin.client.Authenticated += (s, e) =>
            {
                OnPropertyChanged(nameof(IsUserLoggedIn));
            };

            logger.Debug("ViewModel is initialized");
        }

        public bool IsUserLoggedIn => plugin.client.IsUserLoggedIn();

        public RelayCommand<object> RefreshCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(async (progress) =>
                {
                    await plugin.client.RetryLogin();
                }, new GlobalProgressOptions(ResourceProvider.GetString("LOCSteamLoginChecking"), false));
            });
        }

        public RelayCommand<object> LoginCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                plugin.client.Login();
            });
        }

        // Possible race condition!
        // BeginEdit() and first-time accessing
        // Settings is likely overlapped. Playnite
        // set DataContext right before BeginEdit()
        // when the user opens the settings for the
        // first time. When DataContext is set, WPF
        // emits OnDataContextChanged event which
        // I suspect causes race condition.
        //
        // In a nutshell. Cloning MUST be done at
        // the constructor, not here.
        public void BeginEdit()
        {
            
        }

        public void CancelEdit()
        {
            editing = Serialization.GetClone(plugin.client.Settings);
        }

        public void EndEdit()
        {
            plugin.client.Settings = editing;
            plugin.SavePluginSettings(editing);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }
}
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace IsthereanydealCollectionSyncModified
{
    using static Common;

    public class IsthereanydealCollectionSync : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IsthereanydealCollectionSyncSettingsViewModel viewModel;
        private dynamic duplicateHider;
        private readonly LibraryTracker libraryTracker;
        internal readonly IsthereanydealClient client;
        public override Guid Id { get; } = Guid.Parse("8ee803e8-2f7c-4349-b2b9-e74ac3d60b0c");

        public IsthereanydealCollectionSync(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            var settings = LoadPluginSettings<Settings>();

            if (settings is null)
            {
                logger.Warn("No settings found or not loaded. Created new one.");
                settings = new Settings();
            }

            libraryTracker = new LibraryTracker(api);
            client = new IsthereanydealClient(this, settings, logger);
            viewModel = new IsthereanydealCollectionSyncSettingsViewModel(this);

            logger.Info("Completed plugin initialization");
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@" + ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModified"),
                Description = ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModifiedMainMenuImport"),
                Action = (itemArgs) =>
                {
                    ICollection<Game> games = PlayniteApi.Database.Games;
                    var syncHidden = client.Settings.SyncHidden;

                    if (!syncHidden)
                    {
                        games = PlayniteApi.Database.Games.Where((game) => !game.Hidden).ToArray();
                    }

                    Import(games);
                }
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModifiedGameMenuImport"),
                Action = (itemArgs) =>
                {
                    ICollection<Game> games = itemArgs.Games;
                    var hasDh = !(duplicateHider is null);
                    var syncDh = client.Settings.SyncDuplicateHider;

                    if (hasDh && syncDh)
                    {
                        games = itemArgs.Games.SelectMany(GetCopies).ToArray();
                    }
                    
                    Import(games);
                }
            };
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return viewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new IsthereanydealCollectionSyncSettingsView();
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            client.Database.Sync(PlayniteApi.Database);

            var duplicateHiderGuid = Guid.Parse("382f8003-8ed0-4e47-ae93-05b43c9c6c32");

            duplicateHider = PlayniteApi.Addons.Plugins.FirstOrDefault(p => p.Id == duplicateHiderGuid);

            if (!(duplicateHider is null))
            {
                logger.Info("Detected DuplicateHider");
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            if (client.Settings.AutoRunOnLibraryUpdate)
            {
                Import(libraryTracker.AddedGames, true);
            }

            libraryTracker.Reset();
        }

        /// <summary>
        /// Get all copies of a game using DuplicateHider.
        /// </summary>
        /// <param name="game"></param>
        /// <returns></returns>
        private List<Game> GetCopies(Game game)
        {
            if (duplicateHider is null)
            {
                return new List<Game> { game };
            }

            var games = duplicateHider.GetCopies(game);

            if (games is List<Game>)
            {
                return games;
            }
            else
            {
                return new List<Game> { game };
            }
        }

        /// <summary>
        /// Import games to IsThereAnyDeal collection.<br/>
        /// If <paramref name="headless"/> is true, import will run in background and send notification when finished.
        /// </summary>
        /// <param name="games">Games to import</param>
        /// <param name="headless">Show message box (false) or send notification (true) during the process</param>
        public void Import(ICollection<Game> games, bool headless = false)
        {
            logger.Info($"Start importing (headless: {headless})");

            if (!games.HasItems())
            {
                logger.Info("No games to import. Import stops");
                return;
            }

            if (headless)
            {
                if (!client.IsUserLoggedIn())
                {
                    logger.Info("User not logged in. Stop import.");
                    return;
                }

                if (client.Settings.SkipNoSource)
                {
                    games = games.Where(g => !(g.Source is null)).ToArray();
                }

                _ = ActualImport(games).ContinueWith(task =>
                {
                    var res = task.Result;
                    
                    if (res.kind == ImportResultHelper.Kind.Error)
                    {
                        SendErrorNotification(res.text);
                    }
                    else if (res.result.ImportedGames.HasItems())
                    {
                        SendNotification(res.text);
                    }
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            else
            {
                if (!client.IsUserLoggedIn())
                {
                    logger.Info("User not logged in. Stop import.");
                    if (YesNoMessageBox(ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModifiedErrorMessageNotLoggedIn"), ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModifiedErrorCaption")))
                    {
                        PlayniteApi.MainView.OpenPluginSettings(Id);
                    }

                    return;
                }

                string dialogText;

                if (games.Count == 1)
                {
                    var game = games.First();

                    if (client.Settings.SkipNoSource && game.Source is null && !YesNoMessageBox(Localized("LOCIsThereAnyDealCollectionSyncModifiedImportSkipNoSource", game.Name), ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModified")))
                    {
                        return;
                    }

                    dialogText = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportMessageSingle", games.First().Name);
                }
                else
                {
                    if (client.Settings.SkipNoSource)
                    {
                        games = games.Where(g => !(g.Source is null)).ToArray();
                    }
                    dialogText = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportMessageMultiple", games.Count);
                }

                PlayniteApi.Dialogs.ActivateGlobalProgress(new Func<GlobalProgressActionArgs, Task>(async (progressArgs) =>
                {
                    var res = await ActualImport(games);
                    if (res.kind == ImportResultHelper.Kind.Ok)
                    {
                        PlayniteApi.Dialogs.ShowMessage(res.text, ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModified"));

                        if (res.result.FailedGames.HasItems() && client.Settings.FilterFaileds)
                        {
                            PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                            {
                                logger.Info("Filtering failed-to-sync games");
                                FilterPreset preset = new FilterPreset
                                {
                                    Settings = new FilterPresetSettings
                                    {
                                        Category = new IdItemFilterItemProperties(client.Database.Category.Id)
                                    }
                                };
                                PlayniteApi.MainView.ApplyFilterPreset(preset);
                            });
                        }
                    }
                    else
                    {
                        PlayniteApi.Dialogs.ShowErrorMessage(res.text, ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModifiedErrorCaption"));
                    }
                }), new GlobalProgressOptions(dialogText));
            }
        }

        /// <summary>
        /// The actual import process. This never 
        /// throws an exception. If an exception happens
        /// during the execution, it put the error text
        /// in the return value.
        /// </summary>
        /// <param name="games"></param>
        /// <returns></returns>
        private async Task<ImportResultHelper> ActualImport(ICollection<Game> games)
        {
            try
            {
                ImportResult importResult = await client.Import(games);

                var importResultHelper = new ImportResultHelper
                {
                    result = importResult,
                    kind = ImportResultHelper.Kind.Ok,
                    text = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportMixed", games.Count, importResult.ImportedGames.Count,
                        importResult.SkippedGames.Count,
                        importResult.FailedGames.Count)
                };

                if (games.Count == 1)
                {
                    var game = games.First();

                    if (importResult.FailedGames.HasItems())
                    {
                        importResultHelper.text = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportFailureSingle", game.Name);
                    }
                    else if (importResult.SkippedGames.HasItems())
                    {
                        importResultHelper.text = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportSkippedSingle", game.Name);
                    }
                    else
                    {
                        importResultHelper.text = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportSucceedSingle", game.Name);
                    }
                }
                else
                {
                    if (importResult.FailedGames.Count == games.Count)
                    {
                        importResultHelper.text = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportFailureMultiple", games.Count);
                    }
                    else if (importResult.SkippedGames.Count == games.Count)
                    {
                        importResultHelper.text = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportSkippedMultiple", games.Count);
                    }
                    else if (importResult.ImportedGames.Count == games.Count)
                    {
                        importResultHelper.text = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportSucceedMultiple", games.Count);
                    }
                }

                return importResultHelper;
            }
            catch (HttpRequestException ex)
            {
                logger.Error(ex, "Import failed");

                return new ImportResultHelper
                {
                    text = Localized("LOCIsThereAnyDealCollectionSyncModifiedNetworkError", ex.Message),
                    kind = ImportResultHelper.Kind.Error,
                };
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Import failed");
                
                return new ImportResultHelper
                {
                    text = Localized("LOCIsThereAnyDealCollectionSyncModifiedImportError", ex.Message),
                    kind = ImportResultHelper.Kind.Error,
                };
            }
        }

        private bool YesNoMessageBox(string text, string caption)
        {
            var yes = new MessageBoxOption(ResourceProvider.GetString("LOCYesLabel"), false, false);
            var no = new MessageBoxOption(ResourceProvider.GetString("LOCNoLabel"), true, true);
            var res = PlayniteApi.Dialogs.ShowMessage(text, caption, MessageBoxImage.Question, new List<MessageBoxOption> { yes, no });
            return res == yes;
        }

        private string SendNotification(string msg)
        {
            string text = $"{ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModified")}\n\n{msg}";
            string id = Guid.NewGuid().ToString();

            PlayniteApi.Notifications.Add(id, text, NotificationType.Info);

            return id;
        }

        private string SendErrorNotification(string msg)
        {
            string text = $"{ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModified")}\n\n{msg}";
            string id = Guid.NewGuid().ToString();

            PlayniteApi.Notifications.Add(id, text, NotificationType.Error);

            return id;
        }

        private class ImportResultHelper
        {
            public ImportResult result;
            public string text;
            public Kind kind;

            public enum Kind
            {
                Ok,
                Error
            }
        }

        private class LibraryTracker
        {
            public List<Game> AddedGames { get; private set; } = new List<Game>();

            public LibraryTracker(IPlayniteAPI api)
            {
                api.Database.Games.ItemCollectionChanged += (s, e) =>
                {
                    foreach (var game in e.RemovedItems)
                    {
                        AddedGames.Remove(game);
                    }

                    foreach (var game in e.AddedItems)
                    {
                        AddedGames.Add(game);
                    }
                };
            }

            public void Reset()
            {
                // Don't clear() it because Import runs asynchronously. Clearing it causes import failure.
                AddedGames = new List<Game>();
            }
        }
    }
}
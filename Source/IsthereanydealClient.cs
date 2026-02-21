using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace IsthereanydealCollectionSyncModified
{
    using static Common;

    public class IsthereanydealClient
    {
        public event EventHandler Authenticated;

        private readonly ILogger logger;
        private readonly Plugin plugin;
        public ItadApi Api { get; private set; }
        internal string Username { get; private set; }
        private bool isAuthenticated = false;
        internal Settings Settings { get; set; }
        internal DatabaseProxy Database { get; }

        public IsthereanydealClient(Plugin plugin, Settings settings, ILogger logger)
        {
            this.plugin = plugin;
            this.logger = logger;
            Settings = settings;
            Database = DatabaseProxy.LoadOrInit(plugin);
            Api = new ItadApi(this);

            _ = TryInitUsername();
            logger.Debug("Client initialized");
        }

        private async Task InitUsername()
        {
            logger.Info("Getting username");
            Username = await Api.GetUsername();
            // Username might still be null if the user doesn't set a username. So if GetUsername didn't throw, we assume it's authenticated.
            isAuthenticated = true;
            Authenticated?.Invoke(this, EventArgs.Empty);
        }

        private async Task TryInitUsername()
        {
            try
            {
                await InitUsername();
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to get username");
            }
        }

        public async Task<bool> RetryLogin()
        {
            await TryInitUsername();
            return IsUserLoggedIn();
        }

        public bool IsUserLoggedIn()
        {
            return isAuthenticated;
        }

        public void Login()
        {
            logger.Info("Start login");
            var oauth = new OauthCodeExchange();
            var brokenSteamCallback = $"https://isthereanydeal.com/oauth/authorize/?client_id={ItadOauthConstants.CLIENT_ID}";

            using (var webView = plugin.PlayniteApi.WebViews.CreateView(600, 720))
            {
                webView.LoadingChanged += async (s, e) =>
                {
                    string address = webView.GetCurrentAddress();

                    Uri uri = new Uri(address);
                    var censoredQueries = string.Join("&", HttpUtility.ParseQueryString(uri.Query)
                        .AllKeys
                        .Select(q => $"{q}=***"));
					logger.Debug($"WebView: \"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}?{censoredQueries}{uri.Fragment}\"");

					if (address == brokenSteamCallback)
                    {
                        // Workaround this ITAD error, when returning from a redirect back to ITAD from Steam login:
                        // > App Authorization Error
                        // > The authorization grant type is not supported by the authorization server. (Check that all required parameters have been provided)
                        // It seems ITAD is sending an incomplete redirect URL to Steam(?) causing Steam to redirect back to ITAD with missing parameters.
                        // As a workaround, we just retry the login, which will work now that ITAD cookies are set after Steam login.
                        webView.Navigate(oauth.LoginUrl);
                    }

                    try
                    {
                        if (oauth.TryInitCode(address))
                        {
                            await oauth.GetTokens(Api);
                            await InitUsername();
                            webView.Close();
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        webView.Close();
                        plugin.PlayniteApi.Dialogs.ShowErrorMessage(Localized("LOCIsThereAnyDealCollectionSyncModifiedInternerError", ex.Message), ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModifiedErrorCaption"));
                        logger.Error(ex, $"Error in WebView during authentication");
                    }
                    catch (Exception ex)
                    {
                        webView.Close();
                        plugin.PlayniteApi.Dialogs.ShowErrorMessage(Localized("LOCIsThereAnyDealCollectionSyncModifiedAuthenticationError", ex.Message), ResourceProvider.GetString("LOCIsThereAnyDealCollectionSyncModifiedErrorCaption"));
                        logger.Error(ex, $"Error in WebView during authentication");
                    }
                };
                webView.DeleteDomainCookies(ItadOauthConstants.HOST_NAME);
                webView.Navigate(oauth.LoginUrl);
                webView.OpenDialog();
            }
        }

        /// <summary>
        /// Synchronize games to ITAD.
        /// </summary>
        /// <param name="games"></param>
        /// <returns>List of games that failed to synchronize.</returns>
        public async Task<ImportResult> Import(ICollection<Game> games)
        {
            logger.Info($"Importing {games.Count} games");
            var lookUpGameIdTask = Api.LookUpGameId(games.Select(game => game.Name).ToArray());
            var getCopiesTask = Api.GetCopies();
            RemoveCategoryFromDatabase(plugin.PlayniteApi, Database.Category);

            Task<ICollection<string>> getWaitlistTask = null;

            if (!Settings.RemoveFromWaitlist)
            {
                logger.Info($"Plan to remove games from waitlist");
                getWaitlistTask = Api.GetWaitlist();
            }

            IDictionary<string, string> gameIds = await lookUpGameIdTask;
            ICollection<ItadApiCopy> existingCopies = await getCopiesTask;
            var importResult = new ImportResult();
            var copiesTasks = new List<Task>();

            // ITAD return 500 if two copies with the same name
            // with different store are added in the same request.
            // 
            // The current workaround is separating those copies
            // to different requests. A nested list is used where
            // each inner list represent a request input
            // https://github.com/CarrieForle/isthereanydeal-playnite/issues/1
            var toBeAddedCopies = new List<List<ItadApiAddCopyInput>>();
            var toBeUpdatedCopies = new List<ItadApiUpdateCopyInput>();
            var waitlist = getWaitlistTask is null ? null : await getWaitlistTask;

            foreach (Game game in games)
            {
                ItadShop? shop = ItadShopExtension.FromGameSource(game.Source);
                
                string loggerEntry = $"{game.Name}/{game.Source}/{shop?.ToString() ?? "null"}";
                logger.Trace(loggerEntry);

                if (gameIds.TryGetValue(game.Name, out string gameItadId) && !(gameItadId is null))
                {
                    logger.Trace($"{loggerEntry}/{gameItadId}");

                    // Find copy by the same ITAD id;
                    // same shop first then no shop.
                    var copy = existingCopies
                        .Where(c =>
                            c.game.id == gameItadId &&
                            (c.shop is null ||
                            c.MatchShop(shop))
                        )
                        .OrderByDescending(c => c.shop is null)
                        .FirstOrDefault();

                    var note = Settings.SyncNote ? game.Notes : "";

                    if (copy is null)
                    {
                        var toBeAddedCopy = new ItadApiAddCopyInput(gameItadId, false)
                        {
                            shop = shop,
                            note = note,
                            tags = Settings.Tags,
                        };

                        if (shop == ItadShop.Epic)
                        {
                            toBeAddedCopy.redeemed = Settings.RedeemEpic;
                        }

                        bool foundCopy = false;

                        foreach (var copies in toBeAddedCopies)
                        {
                            if (!copies.Exists(c => c.gameId == gameItadId))
                            {
                                copies.Add(toBeAddedCopy);
                                foundCopy = true;
                                break;
                            }
                        }

                        if (!foundCopy)
                        {
							toBeAddedCopies.Add(new List<ItadApiAddCopyInput>
                            {
                                toBeAddedCopy
                            });
						}

                        importResult.ImportedGames.Add(game);

                        continue;
                    }

                    if (Settings.ImportMode == ImportMode.Skip)
                    {
                        importResult.SkippedGames.Add(game);
                        continue;
                    }

                    var toBeUpdatedCopy = new ItadApiUpdateCopyInput(copy.id)
                    {
                        shop = shop,
                        note = note,
                        tags = Settings.Tags,
                    };

                    if (shop == ItadShop.Epic)
                    {
                        toBeUpdatedCopy.redeemed = Settings.RedeemEpic;
                    }

                    toBeUpdatedCopies.Add(toBeUpdatedCopy);
                    importResult.ImportedGames.Add(game);
                }
                else
                {
                    importResult.FailedGames.Add(game);
                }
            }

            logger.Info($"Imported({importResult.ImportedGames.Count})\nSkipped({importResult.SkippedGames.Count})\nFailed({importResult.FailedGames.Count})");

            if (toBeAddedCopies.HasItems())
            {
                logger.Info("Plan to add copy");

                foreach (var copies in toBeAddedCopies)
                {
                    copiesTasks.Add(Api.AddCopies(copies));
                }
            }

            if (toBeUpdatedCopies.HasItems())
            {
                logger.Info("Plan to update copy");
                copiesTasks.Add(Api.UpdateCopies(toBeUpdatedCopies));
            }

			// TODO: It's possible some tasks have completed
            // before one of them throws. Currently the caller
            // has no info which task fails or incomplete in
            // that case, and the user likely doesn' know what
            // to do after this.
            //
            // Either a better error handling or helpful
            // error message will improve this situation.
			var resultTask = Task.WhenAll(copiesTasks);

            if (!Settings.RemoveFromWaitlist && waitlist.HasItems())
            {
                resultTask = resultTask.ContinueWith(async (task) =>
                {
                    // ITAD removes games upon collection, so
                    // re-adding them back
                    logger.Info("Removing games from waitlist");
                    await Api.AddToWaitlist(waitlist);
                }, TaskContinuationOptions.OnlyOnRanToCompletion).Unwrap();
            }

            await resultTask;
            logger.Info("Completed import web requests");

            if (importResult.FailedGames.HasItems())
            {
                if (Settings.FilterFaileds)
                {
                    logger.Info($"Start applying category to failed games");

                    if (!plugin.PlayniteApi.Database.Categories.Contains(Database.Category))
                    {
                        logger.Info("Adding category to Playnite"); plugin.PlayniteApi.Database.Categories.Add(Database.Category);
                    }

                    using (plugin.PlayniteApi.Database.BufferedUpdate())
                        {
                            foreach (var game in importResult.FailedGames) {
                            AddCategory(plugin.PlayniteApi, game, Database.Category);
                        }
                    }
                }
            }

            logger.Info("Completed Import");
            return importResult;
        }
    }

    public class ImportResult
    {
        public ICollection<Game> FailedGames { get; set; } = new List<Game>();
        public ICollection<Game> SkippedGames { get; set; } = new List<Game>();
        public ICollection<Game> ImportedGames { get; set; } = new List<Game>();
    }
}

﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Http;
using System.Web.Http.OData;
using log4net;
using Negri.Wot.Diagnostics;
using Negri.Wot.Sql;
using Negri.Wot.Tanks;

namespace Negri.Wot.Site.Controllers
{
    /// <summary>
    /// Generic APIs on the site
    /// </summary>
    public class GeneralApiController : ApiController
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(GeneralApiController));

        public GeneralApiController()
        {
            ApiAdminKey = ConfigurationManager.AppSettings["ApiAdminKey"];
        }

        private string ApiAdminKey { get; }

        #region Administrative APIs

        [HttpGet]                
        public IHttpActionResult GetStatus(string apiAdminKey)
        {
            if (apiAdminKey != ApiAdminKey)
            {
                Log.Warn($"Invalid API key on {nameof(GetStatus)}: {apiAdminKey}");
                throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Incorrect key! Your try was logged."),
                    ReasonPhrase = "Incorrect key"
                });
            }

            var getter = new FileGetter(GlobalHelper.DataFolder);
            var clans = getter.GetAllRecent().ToArray();

            var moeDate = getter.GetTanksMoe().Values.First().Date;
            var leadersDate = getter.GetTankLeaders().FirstOrDefault()?.Date ?? new DateTime(2017, 03, 25);

            var result = new SiteDiagnostic(clans)
            {
                TanksMoELastDate = moeDate,
                TankLeadersLastDate = leadersDate
            };

            return Ok(result);
        }

        /// <summary>
        /// Triggers Data Cleaning
        /// </summary>
        /// <param name="apiAdminKey">The Administrative API keu</param>
        /// <param name="daysToKeepOnDaily">Days to keep daily generated files, default 1 month</param>
        /// <param name="daysToKeepOnWeekly">Days to keep weekly generated files, default 3 months</param>
        /// <param name="daysToKeepClanFiles">Days to keep clan files, default 2 months</param>
        /// <param name="daysToKeepPlayerFiles">Days to keep player files (or records), default 2 months</param>
        [HttpDelete]
        public IHttpActionResult CleanDataFolders(string apiAdminKey,
            int daysToKeepOnDaily = 4 * 7 + 7,
            int daysToKeepOnWeekly = 3 * 4 * 7 + 7,
            int daysToKeepClanFiles = 2 * 4 * 7 + 7,
            int daysToKeepPlayerFiles = 2 * 4 * 7 + 7)
        {
            try
            {

                if (apiAdminKey != ApiAdminKey)
                {
                    Log.Warn($"Invalid API key on {nameof(CleanDataFolders)}: {apiAdminKey}");
                    throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {                        
                        Content = new StringContent("Incorrect key! Your try was logged."),
                        ReasonPhrase = "Incorrect key"
                    });
                }

                if (daysToKeepOnDaily < 7)
                {
                    throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent($"{nameof(daysToKeepOnDaily)} must be at least 7."),
                        ReasonPhrase = "Incorrect parameters"
                    });
                }

                if (daysToKeepOnWeekly < 28)
                {
                    throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent($"{nameof(daysToKeepOnWeekly)} must be at least 28."),
                        ReasonPhrase = "Incorrect parameters"
                    });
                }

                if (daysToKeepClanFiles < 28)
                {
                    throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent($"{nameof(daysToKeepClanFiles)} must be at least 28."),
                        ReasonPhrase = "Incorrect parameters"
                    });
                }

                if (daysToKeepPlayerFiles < 28)
                {
                    throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent($"{nameof(daysToKeepPlayerFiles)} must be at least 28."),
                        ReasonPhrase = "Incorrect parameters"
                    });
                }

                var sw = Stopwatch.StartNew();

                var rootFolder = GlobalHelper.DataFolder;

                long deleted = 0;
                long bytes = 0;
                var errors = new List<string>();

                void DeletedFileLocal(FileInfo fi)
                {
                    var b = fi.Length;
                    if (!DeleteFile(fi, out var e))
                    {
                        errors.Add(e);
                        if (errors.Count > 1000)
                        {
                            throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                            {
                                Content = new StringContent("Too many errors deleting the files."),
                                ReasonPhrase = "Clean failed"
                            });
                        }
                    }
                    else
                    {
                        deleted++;
                        bytes += b;
                    }
                }

                // The root directory should be always empty of files
                var di = new DirectoryInfo(rootFolder);
                foreach (var fi in di.EnumerateFiles())
                {
                    DeletedFileLocal(fi);
                }

                var regex = new Regex(@"\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

                // MoE, is a daily file
                di = new DirectoryInfo(Path.Combine(rootFolder, "MoE"));
                foreach (var fi in di.EnumerateFiles())
                {
                    var m = regex.Match(fi.Name);
                    if (!m.Success)
                    {
                        continue;
                    }

                    var date = DateTime.ParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var age = (DateTime.UtcNow.Date - date).TotalDays;
                    if (age > daysToKeepOnDaily)
                    {
                        DeletedFileLocal(fi);
                    }
                }

                // Tanks are weekly files
                di = new DirectoryInfo(Path.Combine(rootFolder, "Tanks"));
                foreach (var fi in di.EnumerateFiles())
                {
                    var m = regex.Match(fi.Name);
                    if (!m.Success)
                    {
                        continue;
                    }

                    var date = DateTime.ParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var age = (DateTime.UtcNow.Date - date).TotalDays;
                    if (age > daysToKeepOnWeekly)
                    {
                        DeletedFileLocal(fi);
                    }
                }

                // Clans without any updates
                di = new DirectoryInfo(Path.Combine(rootFolder, "Clans"));
                foreach (var fi in di.EnumerateFiles())
                {
                    var date = fi.LastWriteTimeUtc;
                    var age = (DateTime.UtcNow.Date - date).TotalDays;
                    if (age > daysToKeepClanFiles)
                    {
                        DeletedFileLocal(fi);
                    }
                }

                // Players without any updates on files (old way)
                di = new DirectoryInfo(Path.Combine(rootFolder, "Players"));
                foreach (var fi in di.EnumerateFiles())
                {
                    var date = fi.LastWriteTimeUtc;
                    var age = (DateTime.UtcNow.Date - date).TotalDays;
                    if (age > daysToKeepPlayerFiles)
                    {
                        DeletedFileLocal(fi);
                    }
                }

                // Players without any updates on the database (current way)
                var connectionString = ConfigurationManager.ConnectionStrings["Store"].ConnectionString;
                var db = new KeyStore(connectionString);
                db.CleanOldPlayers(daysToKeepPlayerFiles);

                sw.Stop();

                var result = new
                {
                    TimeTaken = sw.ElapsedMilliseconds,
                    Deleted = deleted,
                    DeletedBytes = bytes,
                    Errors = errors
                };

                return Ok(result);
            }
            catch (HttpResponseException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(ex.Message),
                    ReasonPhrase = "Clean failed"
                });
            }
        }

        [NonAction]
        private static bool DeleteFile(FileSystemInfo fi, out string error)
        {
            error = null;
            try
            {
                fi.Delete();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        

        [HttpPut]
        public IHttpActionResult PutData(PutDataRequest request)
        {
            try
            {
                var sw = Stopwatch.StartNew();

                if (request.ApiKey != ApiAdminKey)
                {
                    Log.Warn($"Invalid API key on {nameof(PutData)}: {request.ApiKey}");
                    throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("Incorrect key! Your try was logged."),
                        ReasonPhrase = "Incorrect key"
                    });
                }
                                
                if (request.Context == "Player")
                {
                    var player = request.GetObject<Player>();
                    if (player == null)
                    {
                        throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.NotAcceptable)
                        {
                            Content = new StringContent("Null Player was sent."),
                            ReasonPhrase = "Null Player"
                        });
                    }

                    var connectionString = ConfigurationManager.ConnectionStrings["Store"].ConnectionString;
                    var db = new KeyStore(connectionString);
                    db.Set(player);
                }
                else
                {
                    throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("Invalid Context."),
                        ReasonPhrase = "Invalid Context."
                    });
                }


                Log.Debug($"PutData in {sw.Elapsed}");
                return Ok();
            }
            catch (HttpResponseException ex)
            {
                Log.Error("PutData", ex);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("PutData", ex);
                throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(ex.Message),
                    ReasonPhrase = "Put data failed"
                });
            }
        }

        #endregion

        #region Clans API

        [HttpGet]
        [EnableQuery]
        public IQueryable<ClanSummary> GetClans()
        {
            var getter = HttpRuntime.Cache.Get("FileGetter", GlobalHelper.CacheMinutes,
                () => new FileGetter(GlobalHelper.DataFolder));

            var clans = getter.GetAllRecent(true).OrderByDescending(c => c.Top15Wn8).Select(c => new ClanSummary(c));
            return clans.AsQueryable();
        }

        [HttpGet]
        public IHttpActionResult GetClan(string clanTag)
        {
            if (string.IsNullOrWhiteSpace(clanTag) || (clanTag.Length > 5))
            {
                throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("A clan tag can't be empty or more than 5 characters."),
                    ReasonPhrase = "Incorrect parameters"
                });
            }

            var getter = HttpRuntime.Cache.Get("FileGetter", GlobalHelper.CacheMinutes,
                () => new FileGetter(GlobalHelper.DataFolder));

            var clan = getter.GetClan(clanTag);

            if (clan == null)
            {
                throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"Can't find clan with the tag [{clanTag}]."),
                    ReasonPhrase = "Clan not found"
                });
            }

            return Ok(clan);
        }

        #endregion

        #region Tanks API

        [HttpGet]
        public IHttpActionResult GetMoE(DateTime? date = null, string tank = null, long? tankId = null)
        {
            var getter = HttpRuntime.Cache.Get("FileGetter", GlobalHelper.CacheMinutes,
                () => new FileGetter(GlobalHelper.DataFolder));

            var moes = getter.GetTanksMoe(date);

            TankMoe[] tanks = null;
            if (tankId.HasValue)
            {
                if (!moes.ContainsKey(tankId.Value))
                {
                    throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent($"Can't find a tank with id {tankId.Value} on the MoE list."),
                        ReasonPhrase = "Tank not found."
                    });
                }
                tanks = new[] {moes[tankId.Value]};
            }
            else if (!string.IsNullOrWhiteSpace(tank))
            {
                var byName = moes.Values.FirstOrDefault(t => t.Name.EqualsCiAi(tank));
                if (byName != null)
                {
                    tanks = new[] { byName };
                }
                else
                {
                    var byFullName = moes.Values
                        .Where(t => t.FullName.ToLowerInvariant().RemoveDiacritics().Contains(tank.ToLowerInvariant().RemoveDiacritics())).ToArray();
                    if (byFullName.Length > 0)
                    {
                        tanks = byFullName;
                    }
                }
            }
            else
            {
                tanks = moes.Values.Where(t => t.Tier >= 5).OrderByDescending(t => t.Tier).ThenBy(t => t.Type)
                    .ThenBy(t => t.Nation).ThenBy(t => t.Nation).ToArray();
            }

            if (tanks == null)
            {
                throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("Can't find any tank with that matches the query."),
                    ReasonPhrase = "Tank not found."
                });
            }

            var r = new
            {
                moes.First().Value.Date,
                PreviousDay = moes.First().Value.Date.AddDays(-1).Date,
                PreviousWeek = moes.First().Value.Date.AddDays(-7).Date,
                PreviousMonth = moes.First().Value.Date.AddDays(-7 * 4).Date,
                Tanks = tanks
            };

            return Ok(r);
        }



        [HttpGet]
        public IHttpActionResult GetWN8(DateTime? date = null)
        {
            var getter = HttpRuntime.Cache.Get("FileGetter", GlobalHelper.CacheMinutes,
                () => new FileGetter(GlobalHelper.DataFolder));

            var wn8 = getter.GetTanksWN8ReferenceValues(date);

            if (wn8 == null)
            {
                throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("Can't find any WN8 expected values."),
                    ReasonPhrase = "WN8 expected values not found"
                });
            }
           
            return Ok(wn8);
        }

        #endregion


    }
}

﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Negri.Wot.Diagnostics;
using Negri.Wot.Mail;
using Negri.Wot.Sql;
using Negri.Wot.Tanks;
using Newtonsoft.Json;

namespace Negri.Wot
{
    internal class Program
    {

        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));

        private static int Main(string[] args)
        {
            try
            {
                ParseParams(args, out var ageHours, out var maxRunMinutes,
                    out var hourToDeleteOldFiles, out var daysToKeepOnDelete, out var calculateMoe,
                    out var calculateReference, out var playersPerMinute, out var utcShiftToCalculate, out var waitForRemote);

                var resultDirectoryXbox = ConfigurationManager.AppSettings["ResultDirectory"];
                var resultDirectoryPs = ConfigurationManager.AppSettings["PsResultDirectory"];

                Log.Info("------------------------------------------------------------------------------------");
                Log.Info("CalculateClanStats iniciando...");
                Log.InfoFormat(
                    "ageHours: {0}; maxRunMinutes: {1}; resultDirectory: {2}; resultDirectoryPs: {3}; utcShiftToCalculate: {4}; waitForRemote: {5}",
                    ageHours, maxRunMinutes, resultDirectoryXbox, resultDirectoryPs, utcShiftToCalculate, waitForRemote);

                var sw = Stopwatch.StartNew();

                var connectionString = ConfigurationManager.ConnectionStrings["Main"].ConnectionString;
                var provider = new DbProvider(connectionString);
                var clans = provider.GetClanCalculateOrder(ageHours).ToArray();
                Log.InfoFormat("{0} clãs devem ser calculados.", clans.Length);

                const int averageOutTimeMinutes = 30;
                const int averageClanSize = 25;
                var maxPerDay = playersPerMinute * 60 * 24 - averageOutTimeMinutes * playersPerMinute;
                var minPerDay = maxPerDay - averageClanSize;
                Log.Info($"playersPerMinute: {playersPerMinute}; maxPerDay: {maxPerDay}; minPerDay: {minPerDay}");

                var recorder = new DbRecorder(connectionString);

                var putterXbox = new FtpPutter(ConfigurationManager.AppSettings["FtpFolder"],
                    ConfigurationManager.AppSettings["FtpUser"],
                    ConfigurationManager.AppSettings["FtpPassworld"]);

                var putterPs = new FtpPutter(ConfigurationManager.AppSettings["PsFtpFolder"],
                    ConfigurationManager.AppSettings["PsFtpUser"],
                    ConfigurationManager.AppSettings["PsFtpPassworld"]);

                var mailSender = new MailSender(ConfigurationManager.AppSettings["SmtpHost"],
                    int.Parse(ConfigurationManager.AppSettings["SmtpPort"]),
                    true, ConfigurationManager.AppSettings["SmtpUsername"],
                    ConfigurationManager.AppSettings["SmtpPassword"])
                {
                    From = new MailAddress(ConfigurationManager.AppSettings["SmtpUsername"],
                        "Calculate Service")
                };

                var webCacheAge = TimeSpan.FromMinutes(1);
                var cacheDirectory = ConfigurationManager.AppSettings["CacheDirectory"];
                var fetcher = new Fetcher(cacheDirectory)
                {
                    WebCacheAge = webCacheAge,
                    WebFetchInterval = TimeSpan.FromSeconds(1),
                    ApplicationId = ConfigurationManager.AppSettings["WgApi"]
                };

                // Obtém o status, por causa da data dos MoE, para disparar o cálculo assíncrono
                DateTime lastMoeXbox, lastMoePs, lastReferencesXbox, lastReferencesPs;
                SiteDiagnostic siteStatusXbox, siteStatusPs;
                try
                {
                    siteStatusXbox = fetcher.GetSiteDiagnostic(
                        ConfigurationManager.AppSettings["RemoteSiteStatusApi"], ConfigurationManager.AppSettings["ApiAdminKey"]);
                    siteStatusPs = fetcher.GetSiteDiagnostic(
                        ConfigurationManager.AppSettings["PsRemoteSiteStatusApi"], ConfigurationManager.AppSettings["ApiAdminKey"]);

                    lastMoeXbox = siteStatusXbox.TanksMoELastDate;
                    lastMoePs = siteStatusPs.TanksMoELastDate;
                    lastReferencesXbox = siteStatusXbox.TankLeadersLastDate;
                    lastReferencesPs = siteStatusPs.TankLeadersLastDate;
                }
                catch (Exception ex)
                {
                    Log.Error("Error getting initial site diagnostics", ex);

                    // Since the site id offline...
                    siteStatusXbox = siteStatusPs = new SiteDiagnostic(null);
                    GetLastDatesFromFiles(resultDirectoryXbox, out lastMoeXbox, out lastReferencesXbox);
                    GetLastDatesFromFiles(resultDirectoryPs, out lastMoePs, out lastReferencesPs);
                }
                
                Log.Info($"lastMoeXbox: {lastMoeXbox:yyyy-MM-dd}");
                Log.Info($"lastMoePs: {lastMoePs:yyyy-MM-dd}");

                Log.Info($"lastReferencesXbox: {lastReferencesXbox:yyyy-MM-dd}");
                Log.Info($"lastReferencesPs: {lastReferencesPs:yyyy-MM-dd}");

                // Dispara as tarefas de calculo gerais
                var calculationTask = Task.Run(() => RunCalculations(calculateReference, calculateMoe,
                    lastMoeXbox, lastMoePs, lastReferencesXbox, lastReferencesPs, provider, recorder, mailSender,
                    resultDirectoryXbox, resultDirectoryPs,
                    putterXbox, putterPs, fetcher, utcShiftToCalculate));

                // Dispara a tarefa de apagar arquivos antigos dos servidores
                var deleteTask = Task.Run(() =>
                {
                    if (DateTime.UtcNow.Hour != hourToDeleteOldFiles || daysToKeepOnDelete < 28)
                    {
                        return;
                    }

                    // Apaga arquivos com a API administrativa
                    var cleanXBox = Task.Run(() =>
                    {
                        try
                        {
                            var cleaner = new Putter(Platform.XBOX, ConfigurationManager.AppSettings["ApiAdminKey"]);
                            cleaner.CleanFiles();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Erro cleaning XBOX", ex);
                        }
                    });

                    var cleanPs = Task.Run(() =>
                    {
                        try
                        {
                            var cleaner = new Putter(Platform.PS, ConfigurationManager.AppSettings["ApiAdminKey"]);
                            cleaner.CleanFiles();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Erro cleaning XBOX", ex);
                        }
                    });

                    if (waitForRemote)
                    {
                        Task.WhenAll(cleanXBox, cleanPs);
                    }

                });

                // Calcula cada clã
                var doneCount = 0;
                var timedOut = false;
                Parallel.For(0, clans.Length, new ParallelOptions { MaxDegreeOfParallelism = 2 }, i =>
                  {
                      if (sw.Elapsed.TotalMinutes > maxRunMinutes)
                      {
                          timedOut = true;
                          return;
                      }

                      var clan = clans[i];

                      Log.InfoFormat("Processando clã {0} de {1}: {2}@{3}...", i + 1, clans.Length, clan.ClanTag,
                          clan.Plataform);
                      var csw = Stopwatch.StartNew();

                      var cc = CalculateClan(clan, provider, recorder);

                      Log.InfoFormat("Calculado clã {0} de {1}: {2}@{3} em {4:N1}s...",
                          i + 1, clans.Length, clan.ClanTag, clan.Plataform, csw.Elapsed.TotalSeconds);

                      if (cc != null)
                      {
                          var fsw = Stopwatch.StartNew();
                          switch (cc.Plataform)
                          {
                              case Platform.XBOX:
                                  {
                                      var fileName = cc.ToFile(resultDirectoryXbox);
                                      Log.InfoFormat("Arquivo de resultado escrito em '{0}'", fileName);

                                      var putTask = Task.Run(() =>
                                      {
                                          try
                                          {
                                              putterXbox.PutClan(fileName);
                                          }
                                          catch (Exception ex)
                                          {
                                              Log.Error($"Error putting XBOX clan file for {cc.ClanTag}", ex);
                                          }
                                      });

                                      if (waitForRemote)
                                      {
                                          putTask.Wait();
                                      }

                                  }
                                  break;
                              case Platform.PS:
                                  {
                                      var fileName = cc.ToFile(resultDirectoryPs);
                                      Log.InfoFormat("Arquivo de resultado escrito em '{0}'", fileName);

                                      var putTask = Task.Run(() =>
                                      {
                                          try
                                          {
                                              putterPs.PutClan(fileName);
                                          }
                                          catch (Exception ex)
                                          {
                                              Log.Error($"Error putting PS clan file for {cc.ClanTag}", ex);
                                          }
                                      });

                                      if (waitForRemote)
                                      {
                                          putTask.Wait();
                                      }
                                  }
                                  break;
                              case Platform.Virtual:
                                  break;
                              default:
                                  throw new ArgumentOutOfRangeException();
                          }
                          Log.InfoFormat("Upload do clã {0} de {1}: {2}@{3} em {4:N1}s...",
                              i + 1, clans.Length, clan.ClanTag, clan.Plataform, fsw.Elapsed.TotalSeconds);
                      }
                      Interlocked.Increment(ref doneCount);
                      Log.InfoFormat("Processado clã {0} de {1}: {2}@{3} em {4:N1}s. {5} totais.",
                          i + 1, clans.Length, clan.ClanTag, clan.Plataform, csw.Elapsed.TotalSeconds, doneCount);
                  });
                var calculationTime = sw.Elapsed;

                // Envia o e-mail de status

                try
                {
                    siteStatusXbox = fetcher.GetSiteDiagnostic(ConfigurationManager.AppSettings["RemoteSiteStatusApi"], ConfigurationManager.AppSettings["ApiAdminKey"]);
                    siteStatusPs = fetcher.GetSiteDiagnostic(ConfigurationManager.AppSettings["PsRemoteSiteStatusApi"], ConfigurationManager.AppSettings["ApiAdminKey"]);
                }
                catch (Exception ex)
                {
                    Log.Error("Error getting final site diagnostics", ex);

                    // The site is offline...
                    siteStatusXbox = siteStatusPs = new SiteDiagnostic(null);
                }

                Log.Debug("Obtendo informações do BD...");
                var dd = provider.GetDataDiagnostic();
                Log.InfoFormat("Filas: {0} jogadores; {1} clans; {2} cálculos.",
                    dd.PlayersQueueLenght, dd.MembershipQueueLenght, dd.CalculateQueueLenght);
                Log.InfoFormat(
                    "{0} jogadores; {1:N0} por dia; {2:N0} por hora; reais: {3:N0}; {4:N0}; {5:N0}; {6:N0}",
                    dd.TotalPlayers, dd.ScheduledPlayersPerDay, dd.ScheduledPlayersPerHour,
                    dd.AvgPlayersPerHourLastDay, dd.AvgPlayersPerHourLast6Hours, dd.AvgPlayersPerHourLast2Hours,
                    dd.AvgPlayersPerHourLastHour);

                Log.Debug("Enviando e-mail...");
                mailSender.SendStatusMessage(siteStatusXbox, siteStatusPs, dd, minPerDay, maxPerDay,
                    calculationTime, doneCount, timedOut);

                if (dd.ScheduledPlayersPerDay < minPerDay || dd.ScheduledPlayersPerDay > maxPerDay)
                {
                    recorder.BalanceClanSchedule(minPerDay, maxPerDay);
                }

                Log.DebugFormat(
                    "DateTime.UtcNow: {0:o}; hourToDeleteOldFiles: {1}; daysToKeepOnDelete: {2}",
                    DateTime.UtcNow, hourToDeleteOldFiles, daysToKeepOnDelete);

                // Espera o cálculo e a deleção terminarem
                Task.WaitAll(calculationTask, deleteTask);

                // Tempo extra para os e-mails terminarem de serem enviados
                Thread.Sleep(TimeSpan.FromMinutes(2));

                Log.InfoFormat("CalculateClanStats terminando normalmente para {1} clãs em {0}.", sw.Elapsed,
                    clans.Length);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex);
                Console.WriteLine(ex);
                return 1;
            }
        }

        private static void GetLastDatesFromFiles(string resultDirectory, out DateTime lastMoe, out DateTime lastLeader)
        {
            var getter = new FileGetter(resultDirectory);
            lastMoe = getter.GetTanksMoe().Values.First().Date;
            lastLeader = getter.GetTankLeaders().FirstOrDefault()?.Date ?? new DateTime(2017, 03, 25);
        }

        private static Clan CalculateClan(ClanPlataform clan, DbProvider provider,
            DbRecorder recorder)
        {
            Log.DebugFormat("Calculando clã {0}@{1}...", clan.ClanTag, clan.Plataform);

            var cc = provider.GetClan(clan.Plataform, clan.ClanId);

            if (cc == null)
            {
                Log.Warn("O clã ainda não teve nenhum membro atualizado.");
                return null;
            }

            if (cc.Count == 0)
            {
                Log.Warn("O clã ainda não teve nenhum membro atualizado.");
                return null;
            }

            Log.InfoFormat("------------------------------------------------------------------");
            Log.InfoFormat("Clã:                     {0}@{1}.{2}", cc.ClanTag, cc.Plataform, cc.ClanId);
            Log.InfoFormat("# Membros:               {0};{1};{2} - Patched: {3}", cc.Count, cc.Active, 0,
                cc.NumberOfPatchedPlayers);
            Log.InfoFormat("Batalhas:                T:{0:N0};A:{1:N0};W:{2:N0}", cc.TotalBattles, cc.ActiveBattles,
                0);

            recorder.SetClanCalculation(cc);

            return cc;
        }


        private static void RunCalculations(bool calculateReference, bool calculateMoe, DateTime? moeLastDateXbox, DateTime? moeLastDatePs, 
            DateTime? lastReferencesXbox, DateTime? lastReferencesPs, DbProvider provider, DbRecorder recorder, MailSender mailSender, 
            string resultDirectory, string resultDirectoryPs, FtpPutter ftpPutterXbox, FtpPutter ftpPutterPs, 
            Fetcher fetcher, int utcShiftToCalculate)
        {
            Debug.Assert(mailSender != null);

            // Obtem os valores esperados de WN8
            if ((DateTime.UtcNow.Hour % 4) == 1)
            {
                HandleWn8ExpectedValues(Platform.XBOX, provider, resultDirectory, ftpPutterXbox, recorder, fetcher);
                HandleWn8ExpectedValues(Platform.PS,   provider, resultDirectoryPs, ftpPutterPs, recorder, fetcher);
            }
            
            if (calculateMoe)
            {
                CalculateMoE(moeLastDateXbox, moeLastDatePs, provider, recorder, mailSender, resultDirectory, resultDirectoryPs, 
                    ftpPutterXbox, ftpPutterPs, utcShiftToCalculate);
            }


            if (calculateReference)
            {
                CalculateTanksReferences(lastReferencesXbox, lastReferencesPs, provider, recorder, mailSender, resultDirectory, resultDirectoryPs, 
                    ftpPutterXbox, ftpPutterPs, utcShiftToCalculate);
            }
        }

        private static void CalculateTanksReferences(DateTime? lastReferencesXbox, DateTime? lastReferencesPs,
            DbProvider provider, DbRecorder recorder, MailSender mailSender, string resultDirectoryXbox,
            string resultDirectoryPs, FtpPutter ftpPutterXbox, FtpPutter ftpPutterPs, int utcShiftToCalculate)
        {
            var csw = Stopwatch.StartNew();
            recorder.CalculateReference(utcShiftToCalculate);
            csw.Stop();
            Log.Debug($"Cálculo das Referências de Tanques feito em {csw.Elapsed.TotalSeconds:N0}.");

            if (csw.Elapsed.TotalMinutes > 1)
            {
                mailSender.Send("Cálculo das Referências de Tanques",
                    $"Cálculo das Referências de Tanques feito em {csw.Elapsed.TotalSeconds:N0}.");
            }

            if (lastReferencesXbox.HasValue)
            {
                PutTanksReferencesOnPlataform(Platform.XBOX, lastReferencesXbox, provider, mailSender, resultDirectoryXbox, ftpPutterXbox, utcShiftToCalculate);
            }

            if (lastReferencesPs.HasValue)
            {
                PutTanksReferencesOnPlataform(Platform.PS, lastReferencesPs, provider, mailSender, resultDirectoryPs, ftpPutterPs, utcShiftToCalculate);
            }
        }

        private static void PutTanksReferencesOnPlataform(Platform platform, DateTime? lastReferences,
            DbProvider provider, MailSender mailSender, string resultDirectory, FtpPutter ftpPutter, int utcShiftToCalculate)
        {
            Debug.Assert(lastReferences != null, nameof(lastReferences) + " != null");
            Log.Debug($"Referências no site {platform}: {lastReferences.Value:yyyy-MM-dd ddd}");
            var cd = DateTime.UtcNow.AddHours(utcShiftToCalculate);
            var previousMonday = cd.PreviousDayOfWeek(DayOfWeek.Monday);
            Log.Debug($"Segunda-Feira anterior: {previousMonday:yyyy-MM-dd ddd}; Current: {cd:o}");

            if (previousMonday <= lastReferences.Value)
            {
                return;
            }

            // Preciso gerar referências e subir
            var references = provider
                .GetTanksReferences(platform, previousMonday).ToArray();
            var referencesDir = Path.Combine(resultDirectory, "Tanks");
            var leaders = new ConcurrentBag<Leader>();
            Parallel.For(0, references.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (i) =>
              {
                  var r = references[i];
                  var tankFile = r.Save(referencesDir);

                  _ = Task.Run(() =>
                  {
                      try
                      {
                          ftpPutter.PutTankReference(tankFile);
                      }
                      catch (Exception ex)
                      {
                          Log.Error($"Error putting tank reference files for {platform} and tank {r.Name}", ex);
                      }
                  });

                  Log.Info($"Escrito e feito upload da referência {tankFile}");

                  foreach (var leader in r.Leaders)
                  {
                      leaders.Add(leader);
                  }
              });

            var json = JsonConvert.SerializeObject(leaders.ToArray(), Formatting.Indented);
            var leadersFile = Path.Combine(referencesDir, $"{previousMonday:yyyy-MM-dd}.Leaders.json");
            File.WriteAllText(leadersFile, json, Encoding.UTF8);

            _ = Task.Run(() =>
            {
                try
                {
                    ftpPutter.PutTankReference(leadersFile);
                }
                catch (Exception ex)
                {
                    Log.Error($"Error putting leader file for {platform}", ex);
                }
            });

            mailSender.Send($"Upload das Referências {platform} para {previousMonday:yyyy-MM-dd} Ok",
                $"Leaderboard: https://{(platform == Platform.PS ? "ps." : "")}wotclans.com.br/Leaderboard/All");
        }

        private static void CalculateMoE(DateTime? moeLastDateXbox, DateTime? moeLastDatePs, DbProvider provider,
            DbRecorder recorder, MailSender mailSender, string resultDirectoryXbox, string resultDirectoryPs,
            FtpPutter ftpPutterXbox, FtpPutter ftpPutterPs, int utcShiftToCalculate)
        {
            var csw = Stopwatch.StartNew();
            recorder.CalculateMoE(utcShiftToCalculate);
            csw.Stop();
            Log.Debug($"Cálculo das MoE feito em {csw.Elapsed.TotalSeconds:N0}.");

            if (csw.Elapsed.TotalMinutes > 1)
            {
                mailSender.Send("Cálculo das MoE", $"Cálculo das MoE feito em {csw.Elapsed.TotalSeconds:N0}.");
            }

            if (moeLastDateXbox.HasValue)
            {
                PutMoEOnPlataform(Platform.XBOX, moeLastDateXbox, provider, mailSender, resultDirectoryXbox, ftpPutterXbox);
            }

            if (moeLastDatePs.HasValue)
            {
                PutMoEOnPlataform(Platform.PS, moeLastDatePs, provider, mailSender, resultDirectoryPs, ftpPutterPs);
            }
        }

        private static void PutMoEOnPlataform(Platform platform, DateTime? moeLastDate,
            DbProvider provider, MailSender mailSender,
            string resultDirectory, FtpPutter ftpPutter)
        {
            Log.Debug($"Verificando atualização de MoE em {platform}");

            Debug.Assert(moeLastDate != null, nameof(moeLastDate) + " != null");
            Log.DebugFormat("Site Date: {0:yyyy-MM-dd}", moeLastDate.Value);

            var dbDate = provider.GetMoe(platform).First().Date;
            Log.DebugFormat("DB Date: {0:yyyy-MM-dd}", dbDate);

            var date = moeLastDate.Value.AddDays(1);
            while (date <= dbDate)
            {
                Log.InfoFormat("Calculando e fazendo upload para {0:yyyy-MM-dd}...", date);
                var moes = provider.GetMoe(platform, date).ToDictionary(t => t.TankId);

                if (moes.Count > 0)
                {
                    var json = JsonConvert.SerializeObject(moes, Formatting.Indented);
                    var file = Path.Combine(resultDirectory, "MoE", $"{date:yyyy-MM-dd}.moe.json");
                    File.WriteAllText(file, json, Encoding.UTF8);
                    Log.DebugFormat("Salvo o MoE em '{0}'", file);

                    _ = Task.Run(() =>
                    {
                        try
                        {
                            ftpPutter.PutMoe(file);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Error putting MoE on {platform}", ex);
                        }
                    });

                    Log.Debug("Feito uploado do MoE");

                    mailSender.Send($"Upload das MoE {platform} para {date:yyyy-MM-dd} Ok",
                        $"MoE: https://{(platform == Platform.PS ? "ps." : "")}wotclans.com.br/Tanks/MoE");
                }
                else
                {
                    Log.ErrorFormat("Os MoEs para {0:yyyy-MM-dd} retornaram 0 tanques!", date);
                }                

                date = date.AddDays(1);
            }

            Log.Debug($"Verificação do MoE completa para {platform}.");
        }

        private static void HandleWn8ExpectedValues(Platform platform, DbProvider provider, string resultDirectory, FtpPutter ftpPutter, DbRecorder recorder, Fetcher fetcher)
        {
            // Aproveito e pego e salvo os dados de WN8
            if ((recorder != null) && (fetcher != null))
            {
                Log.Info($"Pegando dados de WN8 para {platform}...");
                recorder.Set(fetcher.GetXvmWn8ExpectedValuesAsync().Result);
                Log.Info("Dados de WN8 obtidos e salvos.");
            }

            var wn8 = provider.GetWn8ExpectedValues(platform);
            if (wn8 != null)
            {
                var json = JsonConvert.SerializeObject(wn8, Formatting.Indented);
                var file = Path.Combine(resultDirectory, "MoE", $"{wn8.Date:yyyy-MM-dd}.WN8.json");
                File.WriteAllText(file, json, Encoding.UTF8);
                Log.DebugFormat("Salvo o WN8 Expected em '{0}'", file);

                _ = Task.Run(() =>
                {
                    try
                    {
                        ftpPutter.PutMoe(file);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error putting WN8 on {platform}", ex);
                    }
                });

                Log.Debug($"Feito upload do WN8 para {platform}");
            }
        }


        private static void ParseParams(string[] args, out int ageHours, out int maxRunMinutes,
            out int hourToDeleteOldFiles, out int daysToKeepOnDelete, out bool calculateMoe,
            out bool calculateReference, out int playersPerMinute, out int utcShiftToCalculate, out bool waitForRemote)
        {
            ageHours = int.Parse(args[0]);
            maxRunMinutes = int.Parse(args[1]);

            hourToDeleteOldFiles = 3;
            daysToKeepOnDelete = 28 * 3;
            calculateMoe = true;
            calculateReference = true;
            playersPerMinute = 6;
            utcShiftToCalculate = 0;
            waitForRemote = true;

            for (var i = 2; i < args.Length; ++i)
            {
                var arg = args[i];

                if (arg == "NoCalculateMoE")
                {
                    calculateMoe = false;
                }
                else if (arg == "NoCalculateReference")
                {
                    calculateReference = false;
                }
                else if (arg.Contains("HourToDeleteOldFiles:"))
                {
                    hourToDeleteOldFiles = int.Parse(arg.Substring("HourToDeleteOldFiles:".Length));
                }
                else if (arg.Contains("PlayersPerMinute:"))
                {
                    playersPerMinute = int.Parse(arg.Substring("PlayersPerMinute:".Length));
                }
                else if (arg.Contains("UtcShiftToCalculate:"))
                {
                    utcShiftToCalculate = int.Parse(arg.Substring("UtcShiftToCalculate:".Length));
                }
                else if (arg.Contains("WaitForRemote:"))
                {
                    waitForRemote = bool.Parse(arg.Substring("WaitForRemote:".Length));
                }
                else if (arg.Contains("DaysToKeepOnDelete:"))
                {
                    daysToKeepOnDelete = int.Parse(arg.Substring("DaysToKeepOnDelete:".Length));
                    if (daysToKeepOnDelete < 28)
                    {
                        daysToKeepOnDelete = 28;
                    }
                }
            }
        }
    }
}
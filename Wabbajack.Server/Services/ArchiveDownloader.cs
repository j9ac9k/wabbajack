﻿using System;
using System.Reflection.Metadata.Ecma335;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.BuildServer;
using Wabbajack.Common;
using Wabbajack.Lib.Downloaders;
using Wabbajack.Lib.NexusApi;
using Wabbajack.Server.DataLayer;
using Wabbajack.Server.DTOs;

namespace Wabbajack.Server.Services
{
    public class ArchiveDownloader : AbstractService<ArchiveDownloader, int>
    {
        private SqlService _sql;
        private ArchiveMaintainer _archiveMaintainer;
        private NexusApiClient _nexusClient;

        public ArchiveDownloader(ILogger<ArchiveDownloader> logger, AppSettings settings, SqlService sql, ArchiveMaintainer archiveMaintainer) : base(logger, settings, TimeSpan.FromMinutes(10))
        {
            _sql = sql;
            _archiveMaintainer = archiveMaintainer;
        }

        public override async Task<int> Execute()
        {
            _nexusClient ??= await NexusApiClient.Get();
            await _nexusClient.GetUserStatus();
            int count = 0;

            while (true)
            {
                bool useNexus = _nexusClient.HourlyRemaining > 25;

                var nextDownload = await _sql.GetNextPendingDownload(useNexus);

                if (nextDownload == null)
                    break;
                
                if (nextDownload.Archive.Hash != default && _archiveMaintainer.HaveArchive(nextDownload.Archive.Hash))
                {
                    await nextDownload.Finish(_sql);
                    continue;
                }

                if (nextDownload.Archive.State is ManualDownloader.State)
                {
                    await nextDownload.Finish(_sql);
                    continue;
                }

                try
                {
                    _logger.Log(LogLevel.Information, $"Downloading {nextDownload.Archive.State.PrimaryKeyString}");
                    await DownloadDispatcher.PrepareAll(new[] {nextDownload.Archive.State});

                    using var tempPath = new TempFile();
                    await nextDownload.Archive.State.Download(nextDownload.Archive, tempPath.Path);

                    var hash = await tempPath.Path.FileHashAsync();
                    
                    if (nextDownload.Archive.Hash != default && hash != nextDownload.Archive.Hash)
                    {
                        await nextDownload.Fail(_sql, "Invalid Hash");
                        continue;
                    }

                    if (nextDownload.Archive.Size != default &&
                        tempPath.Path.Size != nextDownload.Archive.Size)
                    {
                        await nextDownload.Fail(_sql, "Invalid Size");
                        continue;
                    }
                    nextDownload.Archive.Hash = hash;
                    nextDownload.Archive.Size = tempPath.Path.Size;

                    _logger.Log(LogLevel.Information, $"Archiving {nextDownload.Archive.State.PrimaryKeyString}");
                    await _archiveMaintainer.Ingest(tempPath.Path);

                    _logger.Log(LogLevel.Information, $"Finished Archiving {nextDownload.Archive.State.PrimaryKeyString}");
                    await nextDownload.Finish(_sql);

                }
                catch (Exception ex)
                {
                    await nextDownload.Fail(_sql, ex.ToString());
                }
                
                count++;
            }

            return count;
        }
    }
}
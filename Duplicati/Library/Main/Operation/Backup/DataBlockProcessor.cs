﻿//  Copyright (C) 2015, The Duplicati Team
//  http://www.duplicati.com, info@duplicati.com
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using CoCoL;
using System.Threading.Tasks;
using Duplicati.Library.Main.Database;
using Duplicati.Library.Main.Volumes;
using Duplicati.Library.Main.Operation.Common;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Duplicati.Library.Main.Operation.Backup
{
    /// <summary>
    /// This class receives data blocks, registers then in the database.
    /// New blocks are added to a compressed archive and sent
    /// to the uploader
    /// </summary>
    internal static class DataBlockProcessor
    {
        /// <summary>
        /// The number of bytes to reserve for the file header
        /// in the compressed volume
        /// </summary>
        public const int BlockCompressionOverhead = 1024;

        /// <summary>
        /// A multiplier for protecting against block data
        /// expanding during compression
        /// </summary>
        public const float NonCompressibleExpansionFactor = 1.02f;


        public static Task Run(BackupDatabase database, Options options, ITaskReader taskreader)
        {
            return AutomationExtensions.RunTask(
            new
            {
                LogChannel = Common.Channels.LogChannel.ForWrite,
                Input = Channels.OutputBlocks.ForRead,
                Output = Channels.BackendRequest.ForWrite,
                SpillPickup = Channels.SpillPickup.ForWrite,
            },

            async self =>
            {
                BlockVolumeWriter blockvolume = null;
                var useindex = options.IndexfilePolicy == Options.IndexFileStrategy.Full;
                var indexdata = useindex ? new Library.Utility.FileBackedStringList() : null;

                // The limit for a single volume size
                var maxvolumesize = options.VolumeSize - BlockCompressionOverhead;

                try
                {
                    while(true)
                    {
                        var b = await self.Input.ReadAsync();

                        // Lazy-start a new block volume
                        if (blockvolume == null)
                        {
                            // Before we start a new volume, probe to see if it exists
                            // This will delay creation of volumes for differential backups
                            // There can be a race, such that two workers determine that
                            // the block is missing, but this will be solved by the AddBlock call
                            // which runs atomically
                            if (await database.FindBlockIDAsync(b.HashKey, b.Size) >= 0)
                            {
                                b.TaskCompletion.TrySetResult(false);
                                continue;
                            }

                            blockvolume = new BlockVolumeWriter(options);
                            blockvolume.VolumeID = await database.RegisterRemoteVolumeAsync(blockvolume.RemoteFilename, RemoteVolumeType.Blocks, RemoteVolumeState.Temporary);
                        }

                        var newBlock = await database.AddBlockAsync(b.HashKey, b.Size, blockvolume.VolumeID);
                        b.TaskCompletion.TrySetResult(newBlock);

                        if (newBlock)
                        {
                            // At this point we have registered the block as belonging to the current
                            // volume, but it is possible that there is not enough space to put it in
                            if (blockvolume.Filesize + (b.Size * NonCompressibleExpansionFactor) > maxvolumesize)
                            {
                                BlockVolumeWriter tmpvolume = null;
                                try
                                {
                                    // Start a new volume 
                                    tmpvolume = new BlockVolumeWriter(options);
                                    tmpvolume.VolumeID = await database.RegisterRemoteVolumeAsync(tmpvolume.RemoteFilename, RemoteVolumeType.Blocks, RemoteVolumeState.Temporary);

                                    // Move the current block to the new volume
                                    await database.MoveBlockToVolumeAsync(b.HashKey, b.Size, blockvolume.VolumeID, tmpvolume.VolumeID);

                                    // Close this volume, and send it to upload
                                    blockvolume.Close();
                                    await database.CommitTransactionAsync("CommitAddBlockToOutputFlush");
                                    await self.Output.WriteAsync(new VolumeUploadRequest(blockvolume, true, indexdata));

                                    // Continue with the freshly created volume
                                    blockvolume = tmpvolume;
                                    if (useindex)
                                        indexdata = new Library.Utility.FileBackedStringList();
                                }
                                catch
                                {
                                    // If something goes wrong, we need to clear the temp volume
                                    if (tmpvolume != null && tmpvolume != blockvolume)
                                        try { tmpvolume.Dispose(); }
                                        catch { } // Ignore this and report the original error

                                    throw;
                                }
                            }

#if DEBUG
                            var presize = blockvolume.Filesize;
#endif

                            // Now add the block to the current volume, as we know there is space for it
                            blockvolume.AddBlock(b.HashKey, b.Data, b.Offset, (int)b.Size, b.Hint);
                            if (b.IsBlocklistHashes && useindex)
                                indexdata.Add(VolumeUploadRequest.EncodeBlockListEntry(b.HashKey, b.Size, b.Data));
                                
#if DEBUG
                            var volumesizeincrease = blockvolume.Filesize - presize;
                            var expectedincrease = (b.Size * NonCompressibleExpansionFactor) + BlockCompressionOverhead;

                            if (volumesizeincrease > expectedincrease)
                                Logging.Log.WriteMessage(string.Format("Size increased {0} bytes more than expected when adding {1} to volume", volumesizeincrease - expectedincrease, b.HashKey), Logging.LogMessageType.Warning);
#endif
                        }

                        // We ignore the stop signal, but not the pause and terminate
                        await taskreader.ProgressAsync;
                    }
                }
                catch(Exception ex)
                {
                    if (ex.IsRetiredException())
                    {
                        // If we have collected data, merge all pending volumes into a single volume
                        if (blockvolume != null && blockvolume.SourceSize > 0)
                        {
                            await self.SpillPickup.WriteAsync(new VolumeUploadRequest(blockvolume, true, indexdata));
                        }
                    }

                    throw;
                }
            });
        }



    }
}

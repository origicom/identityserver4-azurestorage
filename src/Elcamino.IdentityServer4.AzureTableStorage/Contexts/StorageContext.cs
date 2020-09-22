﻿// Copyright (c) David Melendez. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using ElCamino.IdentityServer4.AzureStorage.Helpers;
using Microsoft.Azure.Cosmos.Table;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs.Models;

namespace ElCamino.IdentityServer4.AzureStorage.Contexts
{
    public class StorageContext
    {
        protected StorageContext() { }

        public async Task<string> GetBlobContentAsync(string keyNotHashed, BlobContainerClient container)
        {
            BlobClient blob = container.GetBlobClient(KeyGeneratorHelper.GenerateHashValue(keyNotHashed));
            return await GetBlobContentAsync(blob);
        }

        public async Task<string> GetBlobContentAsync(BlobClient blob)
        {
            try
            {
                Response<BlobDownloadInfo> download = await blob.DownloadAsync();
                using (StreamReader sr = new StreamReader(download.Value.Content, Encoding.UTF8))
                {
                    return await sr.ReadToEndAsync();
                }
            }
            catch (RequestFailedException ex)
               when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
            {
                return string.Empty;
            }

        }

        public async Task<(string blobName, int count)> UpdateBlobCacheFileAsync<Entity>(IEnumerable<Entity> entities, BlobContainerClient cacheContainer)
        {
            DateTime dateTimeNow = DateTime.UtcNow;
            string blobName = KeyGeneratorHelper.GenerateDateTimeDecendingId(dateTimeNow);
            await SaveBlobAsync(blobName, JsonConvert.SerializeObject(entities), cacheContainer);
            return (blobName, count: entities.Count());
        }

        public async Task<(string blobName, int count)> UpdateBlobCacheFileAsync<Entity>(IAsyncEnumerable<Entity> entities, BlobContainerClient cacheContainer)
        {
            return await UpdateBlobCacheFileAsync(await entities.ToListAsync(), cacheContainer);
        }

        /// <summary>
        /// Deletes all blobs older than the latestBlobName by name comparsion
        /// </summary>
        /// <param name="latestBlobName"></param>
        /// <param name="cacheContainer"></param>
        /// <returns></returns>
        public async Task DeleteBlobCacheFilesAsync(string latestBlobName, BlobContainerClient cacheContainer, ILogger logger)
        {
            await foreach (BlobItem blobName in cacheContainer.GetBlobsAsync(BlobTraits.None, BlobStates.None))
            {
                if (String.Compare(latestBlobName, blobName.Name) == -1)
                {
                    BlobClient blobClient = cacheContainer.GetBlobClient(blobName.Name);
                    await blobClient.DeleteAsync();
                    logger.LogInformation($"container: {cacheContainer.Name} blob: {blobName.Name} - cache file deleted");
                }
            }
        }

        public async Task<IEnumerable<Entity>> GetLatestFromCacheBlobAsync<Entity>(BlobContainerClient cacheContainer)
        {
            BlobClient blob = await GetFirstBlobAsync(cacheContainer);
            if (blob != null)
            {
                var entities = await GetEntityBlobAsync<List<Entity>>(blob);
                if (entities != null)
                {
                    return entities;
                }
            }
            return null;
        }

        public async Task<Entity> GetEntityBlobAsync<Entity>(string keyNotHashed, BlobContainerClient container) where Entity : class, new()
        {
            BlobClient blob = container.GetBlobClient(KeyGeneratorHelper.GenerateHashValue(keyNotHashed));
            return await GetEntityBlobAsync<Entity>(blob);
        }

        public async Task<Entity> GetEntityBlobAsync<Entity>(BlobClient blobJson) where Entity : class, new()
        {
            try
            {
                var download = await blobJson.DownloadAsync();
                using (Stream s = download.Value.Content)
                {
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        using (JsonReader reader = new JsonTextReader(sr))
                        {
                            JsonSerializer serializer = new JsonSerializer();
                            return serializer.Deserialize<Entity>(reader);
                        }
                    }
                }
            }
            catch (RequestFailedException ex)
               when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
            {
                return null;
            }
        }

        public async IAsyncEnumerable<Entity> GetAllBlobEntitiesAsync<Entity>(BlobContainerClient container, ILogger logger) where Entity : class, new()
        {
            await foreach(var blobJson in GetAllBlobsAsync(container))
            {
                yield return await GetEntityBlobAsync<Entity>(blobJson);
            }
        }
       

        public async IAsyncEnumerable<BlobClient> GetAllBlobsAsync(BlobContainerClient container)
        {
            AsyncPageable<BlobItem> pageable = container.GetBlobsAsync(BlobTraits.None, BlobStates.None, string.Empty);
            IAsyncEnumerable<Page<BlobItem>> pages = pageable.AsPages();
            await foreach (Page<BlobItem> page in pages)
            {
                foreach(BlobItem blob in page.Values)
                {
                    yield return container.GetBlobClient(blob.Name);
                }
            }
        }

        public async Task<BlobClient> GetFirstBlobAsync(BlobContainerClient container)
        {
            AsyncPageable<BlobItem> pageable = container.GetBlobsAsync(BlobTraits.None, BlobStates.None, string.Empty);
            IAsyncEnumerable<Page<BlobItem>> pages = pageable.AsPages(pageSizeHint:1);

            await foreach(Page<BlobItem> page in pages)
            {
                BlobItem blob = page?.Values?.FirstOrDefault();
                if (blob != null)
                {
                    return container.GetBlobClient(blob.Name);
                }
                break;
            }
            
            return null;
        }       

        public async Task DeleteBlobAsync(string keyNotHashed, BlobContainerClient container)
        {
            BlobClient blob = container.GetBlobClient(KeyGeneratorHelper.GenerateHashValue(keyNotHashed));
            await blob.DeleteIfExistsAsync();
        }

        public async Task SaveBlobWithHashedKeyAsync(string keyNotHashed, string jsonEntityContent, BlobContainerClient container)
        {
            BlobClient blob = container.GetBlobClient(KeyGeneratorHelper.GenerateHashValue(keyNotHashed));

            await blob.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonEntityContent)), new BlobHttpHeaders() 
            { 
                ContentType = "application/json"
            });
        }

        public async Task SaveBlobAsync(string blobName, string jsonEntityContent, BlobContainerClient container)
        {
            BlobClient blob = container.GetBlobClient(blobName);
            await blob.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(jsonEntityContent)), new BlobHttpHeaders()
            {
                ContentType = "application/json"
            });
        }
       
        /// <summary>
        /// Get table entity where the partition and row key are the hash of the entity key provided
        /// </summary>
        /// <typeparam name="Entity"></typeparam>
        /// <param name="keyNotHashed">Key of the entity (not hashed)</param>
        /// <returns></returns>
        public async Task<Entity> GetEntityTableAsync<Entity>(string keyNotHashed, CloudTable table) 
            where Entity : class, ITableEntity, new()
        {
            string hashedKey = KeyGeneratorHelper.GenerateHashValue(keyNotHashed);
            var r = await table.ExecuteAsync(TableOperation.Retrieve<Entity>(hashedKey, hashedKey));
            return r.Result as Entity;
        }

        public async Task GetAndDeleteTableEntityByKeysAsync(string partitionKey, string rowKey, CloudTable table)
        {
            var entity = (await table.ExecuteAsync(TableOperation.Retrieve(partitionKey, rowKey))).Result as ITableEntity;
            if (entity != null)
            {
                await table.ExecuteAsync(TableOperation.Delete(entity));
            }

        }

        
        /// <summary>
        /// Performs better than using ExecuteQuerySegmentedAsync and building an internal list
        /// </summary>
        /// <typeparam name="Entity"></typeparam>
        /// <param name="tableQuery"></param>
        /// <param name="table"></param>
        /// <returns></returns>
        public Task<IEnumerable<Entity>> GetAllByTableQueryAsync<Entity>(TableQuery<Entity> tableQuery, CloudTable table)
           where Entity : class, ITableEntity, new()
        {
            return Task.Run<IEnumerable<Entity>>(() => GetAllByTableQuery(tableQuery, table));
        }

        public IEnumerable<Entity> GetAllByTableQuery<Entity>(TableQuery<Entity> tableQuery, CloudTable table)
           where Entity : class, ITableEntity, new()
        {
            TableContinuationToken continuationToken = new TableContinuationToken();
            while (continuationToken != null)
            {
                var tableResults = table.ExecuteQuerySegmented<Entity>(tableQuery, continuationToken);
                foreach (var result in tableResults.Results)
                {
                    yield return result;
                }
                continuationToken = tableResults.ContinuationToken;
            }
        }
    }

}

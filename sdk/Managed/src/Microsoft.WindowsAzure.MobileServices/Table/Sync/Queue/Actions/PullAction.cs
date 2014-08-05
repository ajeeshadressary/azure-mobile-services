﻿// ----------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.MobileServices.Query;
using Newtonsoft.Json.Linq;

namespace Microsoft.WindowsAzure.MobileServices.Sync
{
    internal class PullAction : TableAction
    {
        private static readonly QueryNode updatedAtNode = new MemberAccessNode(null, MobileServiceSystemColumns.UpdatedAt);
        private static readonly OrderByNode orderByUpdatedAtNode = new OrderByNode(updatedAtNode, OrderByDirection.Ascending);
        private IDictionary<string, string> parameters;
        private DateTimeOffset maxUpdatedAt = DateTimeOffset.MinValue;

        public PullAction(MobileServiceTable table,
                          MobileServiceSyncContext context,
                          string queryKey,
                          MobileServiceTableQueryDescription query,
                          IDictionary<string, string> parameters,
                          OperationQueue operationQueue,
                          MobileServiceSyncSettingsManager settings,
                          IMobileServiceLocalStore store,
                          CancellationToken cancellationToken)
            : base(table, queryKey, query, context, operationQueue, settings, store, cancellationToken)
        {
            this.parameters = parameters;
        }

        protected override bool CanDeferIfDirty
        {
            get { return true; }
        }

        protected async override Task ProcessTableAsync()
        {
            bool incrementalSync = this.IsIncrementalSync();
            DateTimeOffset deltaToken = this.maxUpdatedAt;

            if (incrementalSync)
            {
                this.Table.SystemProperties = this.Table.SystemProperties | MobileServiceSystemProperties.UpdatedAt;
                deltaToken = await this.Settings.GetDeltaTokenAsync(this.Query.TableName, this.QueryKey);
                ApplyDeltaToken(this.Query, deltaToken);
            }

            JToken remoteResults = await this.Table.ReadAsync(this.Query.ToQueryString(), MobileServiceTable.IncludeDeleted(parameters));
            var result = QueryResult.Parse(remoteResults);

            this.CancellationToken.ThrowIfCancellationRequested();

            await this.ProcessAll(result.Values);

            if (incrementalSync && this.maxUpdatedAt > deltaToken)
            {
                await this.Settings.SetDeltaTokenAsync(this.Query.TableName, this.QueryKey, this.maxUpdatedAt.ToUniversalTime());
            }
        }

        private async Task ProcessAll(JArray items)
        {
            var deletedIds = new List<string>();
            var upsertList = new List<JObject>();

            foreach (JObject item in items)
            {
                string id = (string)item[MobileServiceSystemColumns.Id];
                if (id == null)
                {
                    continue;
                }

                DateTimeOffset updatedAt = GetUpdatedAt(item);
                if (updatedAt > this.maxUpdatedAt)
                {
                    this.maxUpdatedAt = updatedAt;
                }

                if (IsDeleted(item))
                {
                    deletedIds.Add(id);
                }
                else
                {
                    upsertList.Add(item);
                }
            }

            if (upsertList.Any())
            {
                await this.Store.UpsertAsync(this.Table.TableName, upsertList, fromServer: true);
            }

            if (deletedIds.Any())
            {
                await this.Store.DeleteAsync(this.Table.TableName, deletedIds);
            }
        }

        private bool IsIncrementalSync()
        {
            return !String.IsNullOrEmpty(this.QueryKey);
        }

        private void ApplyDeltaToken(MobileServiceTableQueryDescription query, DateTimeOffset deltaToken)
        {
            query.Ordering.Insert(0, orderByUpdatedAtNode);
            // .NET runtime system properties are of datetimeoffset type so we'll use the datetimeoffset odata token
            QueryNode tokenNode = new ConstantNode(deltaToken);
            QueryNode updatedAtGreaterThanOrEqualNode = new BinaryOperatorNode(BinaryOperatorKind.GreaterThanOrEqual, updatedAtNode, tokenNode);
            query.Filter = query.Filter == null ? updatedAtGreaterThanOrEqualNode : new BinaryOperatorNode(BinaryOperatorKind.And, query.Filter, updatedAtGreaterThanOrEqualNode);
        }

        private static bool IsDeleted(JObject item)
        {
            JToken deletedToken = item[MobileServiceSystemColumns.Deleted];
            bool isDeleted = deletedToken != null && deletedToken.Value<bool>();
            return isDeleted;
        }

        private static DateTimeOffset GetUpdatedAt(JObject item)
        {
            DateTimeOffset updatedAt = DateTimeOffset.MinValue;
            JToken updatedAtToken = item[MobileServiceSystemColumns.UpdatedAt];
            if (updatedAtToken != null)
            {
                updatedAt = updatedAtToken.ToObject<DateTimeOffset>();
            }
            return updatedAt;
        }
    }
}
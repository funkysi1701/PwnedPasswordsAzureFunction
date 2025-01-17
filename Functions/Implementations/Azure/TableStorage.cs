﻿namespace HaveIBeenPwned.PwnedPasswords.Implementations.Azure;

public class TableStorageOptions : IOptions<TableStorageOptions>
{
    public string ConnectionString { get; set; } = "";
    public string Namespace { get; set; } = "";
    public TableStorageOptions Value => this;
}

/// <summary>
/// Table Storage wrapper 
/// </summary>
public sealed class TableStorage : ITableStorage
{
    private readonly TableClient _transactionTable;
    private readonly TableClient _dataTable;
    private readonly TableClient _hashDataTable;
    private readonly TableClient _cachePurgeTable;
    private readonly ILogger _log;

    public TableStorage(IOptions<TableStorageOptions> options, ILogger<TableStorage> log, TableServiceClient serviceClient)
    {
        _log = log;
        _transactionTable = serviceClient.GetTableClient($"{options.Value.Namespace}transactions");
        _dataTable = serviceClient.GetTableClient($"{options.Value.Namespace}transactiondata");
        _hashDataTable = serviceClient.GetTableClient($"{options.Value.Namespace}hashdata");
        _cachePurgeTable = serviceClient.GetTableClient($"{options.Value.Namespace}cachepurge");
        _transactionTable.CreateIfNotExists();
        _dataTable.CreateIfNotExists();
        _hashDataTable.CreateIfNotExists();
        _cachePurgeTable.CreateIfNotExists();
    }

    public async Task<PwnedPasswordsTransaction> InsertAppendDataAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        var transaction = new PwnedPasswordsTransaction { TransactionId = Guid.NewGuid().ToString() };
        await _transactionTable.AddEntityAsync(new AppendTransactionEntity { PartitionKey = subscriptionId, RowKey = transaction.TransactionId, Confirmed = false }, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("Subscription {SubscriptionId} created a new transaction with id = {TransactionId}.", subscriptionId, transaction.TransactionId);
        return transaction;
    }

    public async Task<bool> ConfirmAppendDataAsync(string subscriptionId, PwnedPasswordsTransaction transaction, CancellationToken cancellationToken = default)
    {
        try
        {
            Response<AppendTransactionEntity> transactionEntityResponse = await _transactionTable.GetEntityAsync<AppendTransactionEntity>(subscriptionId, transaction.TransactionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!transactionEntityResponse.Value.Confirmed)
            {
                transactionEntityResponse.Value.Confirmed = true;
                Response? updateResponse = await _transactionTable.UpdateEntityAsync(transactionEntityResponse.Value, transactionEntityResponse.Value.ETag, cancellationToken: cancellationToken).ConfigureAwait(false);
                _log.LogInformation("Subscription {SubscriptionId} successfully confirmed transaction {TransactionId}. Queueing data for blob updates.", subscriptionId, transaction.TransactionId);
                return true;
            }

            // We've already confirmed this transaction.
            return false;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            throw new ArgumentOutOfRangeException("Transaction id not found.", e);
        }
        catch (RequestFailedException e) when (e.Status == StatusCodes.Status409Conflict)
        {
            throw new ArgumentException("Transaciton has already been updated.", e);
        }
        catch (RequestFailedException e)
        {
            _log.LogError(e, "Error looking up/updating transaction with id = {TransactionId} for subscription {SubscriptionId}.", transaction.TransactionId, subscriptionId);
            throw new InvalidOperationException("Error confirming transaction.", e);
        }
    }

    public async Task<bool> IsTransactionConfirmedAsync(string subscriptionId, string transactionId, CancellationToken cancellationToken = default)
    {
        try
        {
            Response<AppendTransactionEntity> transactionEntityResponse = await _transactionTable.GetEntityAsync<AppendTransactionEntity>(subscriptionId, transactionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return transactionEntityResponse.Value.Confirmed;
        }
        catch (RequestFailedException e)
        {
            throw new ArgumentException("Transaction not found.", nameof(transactionId), e);
        }
    }

    public async Task<List<PwnedPasswordsIngestionValue>> GetTransactionValuesAsync(string subscriptionId, string transactionId, CancellationToken cancellationToken = default)
    {
        var entries = new List<PwnedPasswordsIngestionValue>();
        AsyncPageable<AppendDataEntity> transactionDataResponse = _dataTable.QueryAsync<AppendDataEntity>(x => x.PartitionKey == transactionId, cancellationToken: cancellationToken);
        await foreach (AppendDataEntity item in transactionDataResponse)
        {
            entries.Add(new PwnedPasswordsIngestionValue { SHA1Hash = item.RowKey, NTLMHash = item.NTLMHash, Prevalence = item.Prevalence });
        }

        return entries;
    }

    public async Task<bool> AddOrIncrementHashEntry(PasswordEntryBatch batch, PwnedPasswordsIngestionValue value, CancellationToken cancellationToken = default)
    {
        string partitionKey = value.SHA1Hash[..5];
        string rowKey = value.SHA1Hash[5..];

        try
        {
            try
            {
                Response<PwnedPasswordEntity> entityResponse = await _hashDataTable.GetEntityAsync<PwnedPasswordEntity>(partitionKey, rowKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                PwnedPasswordEntity pwnedPassword = entityResponse.Value;
                pwnedPassword.Prevalence += value.Prevalence;
                await _hashDataTable.UpdateEntityAsync(pwnedPassword, pwnedPassword.ETag, cancellationToken: cancellationToken).ConfigureAwait(false);
                _log.LogInformation("Subscription {SubscriptionId} updated SHA1 entry {SHA1} from {PrevalenceBefore} to {PrevalenceAfter} as part of transaction {TransactionId}", batch.SubscriptionId, value.SHA1Hash, pwnedPassword.Prevalence - value.Prevalence, pwnedPassword.Prevalence, batch.TransactionId);
            }
            // If the item doesn't exist
            catch (RequestFailedException e) when (e.Status == StatusCodes.Status404NotFound)
            {
                await _hashDataTable.AddEntityAsync(new PwnedPasswordEntity { PartitionKey = partitionKey, RowKey = rowKey, NTLMHash = value.NTLMHash, Prevalence = value.Prevalence }, cancellationToken).ConfigureAwait(false);
                _log.LogInformation("Subscription {SubscriptionId} added new SHA1 entry {SHA1} with {Prevalence} as part of transaction {TransactionId}", batch.SubscriptionId, value.SHA1Hash, value.Prevalence, batch.TransactionId);
            }
        }
        catch (RequestFailedException e) when (e.Status == StatusCodes.Status412PreconditionFailed || e.Status == StatusCodes.Status409Conflict)
        {
            _log.LogWarning(e, $"Unable to update or insert PwnedPasswordEntity {partitionKey}:{rowKey} as it has already been updated.");
            return false;
        }

        return true;
    }

    public async Task MarkHashPrefixAsModified(string prefix, CancellationToken cancellationToken = default)
    {
        await _cachePurgeTable.UpsertEntityAsync(new TableEntity($"{DateTime.UtcNow.Year}-{DateTime.UtcNow.Month}-{DateTime.UtcNow.Day}", prefix), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Get the modified blobs since yesterday
    /// </summary>
    /// <returns>List of partition keys which have been modified</returns>
    public async Task<List<string>> GetModifiedHashPrefixes(CancellationToken cancellationToken = default)
    {
        DateTime yesterday = DateTime.UtcNow.AddDays(-1);
        AsyncPageable<TableEntity> results = _cachePurgeTable.QueryAsync<TableEntity>(x => x.PartitionKey == $"{yesterday.Year}-{yesterday.Month}-{yesterday.Day}", cancellationToken: cancellationToken);
        var prefixes = new List<string>();
        await foreach (TableEntity item in results)
        {
            if (item != null)
            {
                prefixes.Add(item.RowKey);
            }
        }

        return prefixes;
    }
}

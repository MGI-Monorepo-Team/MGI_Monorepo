using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Single entry point for all wallet operations for a player.
/// Persists wallet and transaction state under FilePathResolver economy paths.
/// Automatically migrates legacy editor/test files when canonical files are missing.
/// </summary>
public class EconomyService
{
    [Serializable]
    private class WalletUpdatedPayload
    {
        public string player_id;
        public int coins;
        public int gems;
        public int coaching_credits;
        public string source;
        public string timestamp;
    }

    private const string WalletFileName = "wallet.json";
    private const string TransactionsFileName = "wallet_transactions.json";
    private const string LegacyUppercaseEconomyFolderName = "Economy";
    private const string LegacyStreamingAssetsFolderName = "StreamingAssets";

    /// <summary>
    /// Retained for backward compatibility with older callers and editor tools.
    /// Economy files are now always read and written via FilePathResolver.
    /// In the Unity Editor, the legacy StreamingAssets files are also mirrored
    /// for debugging so developers can inspect a stable, known location.
    /// </summary>
    public bool UseStreamingAssetsForEconomyFiles { get; set; } = true;

    /// <summary>
    /// Toggle this off if a caller needs silent wallet updates.
    /// </summary>
    public bool PublishWalletUpdatedEvents { get; set; } = true;

    public Wallet GetWallet(string playerId, bool createIfMissing = true)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            Debug.LogWarning("[EconomyService] GetWallet called with empty playerId.");
            return null;
        }

        var walletPath = GetCanonicalEconomyFilePath(playerId, WalletFileName);
        var wallet = ReadWalletForPlayer(walletPath, playerId);
        if (wallet != null)
        {
            EnsureTransactionsFile(playerId);
            MirrorEditorDebugWallet(wallet);
            return wallet;
        }

        if (TryMigrateLegacyWallet(playerId, walletPath, out var migratedWallet))
        {
            EnsureTransactionsFile(playerId);
            return migratedWallet;
        }

        if (!createIfMissing)
        {
            return null;
        }

        var created = CreateDefaultWallet(playerId);
        WriteJsonAtomic(walletPath, created);
        MirrorEditorDebugWallet(created);
        EnsureTransactionsFile(playerId);
        TrySynchronizeForecast(playerId);
        return created;
    }

    public bool TrySpend(string playerId, int coins, int gems, string source)
    {
        return TrySpend(playerId, coins, gems, source, out _);
    }

    public bool TrySpend(string playerId, int coins, int gems, string source, out Wallet updatedWallet)
    {
        return TrySpend(playerId, coins, gems, 0, source, out updatedWallet);
    }

    public bool TrySpend(string playerId, int coins, int gems, int coachingCredits, string source)
    {
        return TrySpend(playerId, coins, gems, coachingCredits, source, out _);
    }

    public bool TrySpend(string playerId, int coins, int gems, int coachingCredits, string source, out Wallet updatedWallet)
    {
        updatedWallet = GetWallet(playerId, true);
        if (updatedWallet == null)
        {
            return false;
        }

        if (coins < 0 || gems < 0 || coachingCredits < 0)
        {
            Debug.LogWarning("[EconomyService] TrySpend rejected negative spend input.");
            return false;
        }

        if (coins == 0 && gems == 0 && coachingCredits == 0)
        {
            return true;
        }

        if (updatedWallet.coins < coins ||
            updatedWallet.gems < gems ||
            updatedWallet.coaching_credits < coachingCredits)
        {
            return false;
        }

        updatedWallet.coins -= coins;
        updatedWallet.gems -= gems;
        updatedWallet.coaching_credits -= coachingCredits;
        updatedWallet.last_updated = UtcNowIso();

        PersistWalletAndTransactions(
            playerId,
            updatedWallet,
            BuildTransactions(playerId, coins, gems, coachingCredits, "spend", source));

        PublishWalletUpdated(playerId, source, updatedWallet);
        return true;
    }

    public void AddCurrency(string playerId, int coins, int gems, string source)
    {
        AddCurrency(playerId, coins, gems, 0, source);
    }

    public void AddCurrency(string playerId, int coins, int gems, int coachingCredits, string source)
    {
        AddCurrencyInternal(playerId, coins, gems, coachingCredits, source, null, out _);
    }

    /// <summary>
    /// Adds a reward at most once for a stable domain event. Replaying an imported
    /// simulator result returns true but leaves the wallet unchanged.
    /// </summary>
    public bool TryAddCurrencyOnce(string playerId, int coins, int gems, int coachingCredits,
        string source, string eventId, out bool applied)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            Debug.LogWarning("[EconomyService] TryAddCurrencyOnce requires a non-empty eventId.");
            applied = false;
            return false;
        }

        return AddCurrencyInternal(playerId, coins, gems, coachingCredits, source, eventId, out applied);
    }

    private bool AddCurrencyInternal(string playerId, int coins, int gems, int coachingCredits,
        string source, string eventId, out bool applied)
    {
        applied = false;
        var wallet = GetWallet(playerId, true);
        if (wallet == null)
        {
            return false;
        }

        if (coins < 0 || gems < 0 || coachingCredits < 0)
        {
            Debug.LogWarning("[EconomyService] AddCurrency rejected negative input.");
            return false;
        }

        if (coins == 0 && gems == 0 && coachingCredits == 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(eventId) &&
            LoadTransactions(playerId).Any(transaction => transaction != null && transaction.event_id == eventId))
        {
            return true;
        }

        wallet.coins += coins;
        wallet.gems += gems;
        wallet.coaching_credits += coachingCredits;
        wallet.last_updated = UtcNowIso();

        PersistWalletAndTransactions(
            playerId,
            wallet,
            BuildTransactions(playerId, coins, gems, coachingCredits, "earn", source, eventId));

        PublishWalletUpdated(playerId, source, wallet);
        applied = true;
        return true;
    }

    public bool ResetWallet(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            Debug.LogWarning("[EconomyService] ResetWallet called with empty playerId.");
            return false;
        }

        var wallet = CreateDefaultWallet(playerId);
        var walletPath = GetCanonicalEconomyFilePath(playerId, WalletFileName);
        var transactionsPath = GetCanonicalEconomyFilePath(playerId, TransactionsFileName);

        WriteJsonAtomic(walletPath, wallet);
        WriteJsonAtomic(transactionsPath, new List<WalletTransaction>());
        MirrorEditorDebugWallet(wallet);
        MirrorEditorDebugTransactions(new List<WalletTransaction>());
        TrySynchronizeForecast(playerId);
        PublishWalletUpdated(playerId, "reset_wallet", wallet);
        return true;
    }

    public IEnumerable<WalletTransaction> GetRecentTransactions(string playerId, int limit)
    {
        if (string.IsNullOrWhiteSpace(playerId) || limit <= 0)
        {
            return Enumerable.Empty<WalletTransaction>();
        }

        EnsureTransactionsFile(playerId);
        var allTransactions = LoadTransactions(playerId);

        return allTransactions
            .Where(t => t != null && t.player_id == playerId)
            .OrderByDescending(t => ParseTimestampOrMin(t.timestamp))
            .Take(limit)
            .ToList();
    }

    private static Wallet CreateDefaultWallet(string playerId)
    {
        return new Wallet
        {
            player_id = playerId,
            coins = 0,
            gems = 0,
            coaching_credits = 0,
            last_updated = UtcNowIso()
        };
    }

    private void EnsureTransactionsFile(string playerId)
    {
        var path = GetCanonicalEconomyFilePath(playerId, TransactionsFileName);
        if (File.Exists(path))
        {
            var existingTransactions = ReadTransactionsForPlayer(path, playerId);
            if (existingTransactions != null)
            {
                MirrorEditorDebugTransactions(existingTransactions);
            }
            return;
        }

        if (TryMigrateLegacyTransactions(playerId, path, out _))
        {
            return;
        }

        var emptyTransactions = new List<WalletTransaction>();
        WriteJsonAtomic(path, emptyTransactions);
        MirrorEditorDebugTransactions(emptyTransactions);
    }

    private void PersistWalletAndTransactions(string playerId, Wallet wallet, List<WalletTransaction> newEntries)
    {
        var walletPath = GetCanonicalEconomyFilePath(playerId, WalletFileName);
        var transactionsPath = GetCanonicalEconomyFilePath(playerId, TransactionsFileName);

        var existingTransactions = LoadTransactions(playerId);
        existingTransactions.AddRange(newEntries);

        WriteJsonAtomic(walletPath, wallet);
        WriteJsonAtomic(transactionsPath, existingTransactions);
        MirrorEditorDebugWallet(wallet);
        MirrorEditorDebugTransactions(existingTransactions);
        TrySynchronizeForecast(playerId);
    }

    private List<WalletTransaction> BuildTransactions(
        string playerId,
        int coins,
        int gems,
        int coachingCredits,
        string type,
        string source,
        string eventId = null)
    {
        var list = new List<WalletTransaction>();
        var timestamp = UtcNowIso();
        var safeSource = string.IsNullOrWhiteSpace(source) ? "unknown_source" : source;

        if (coins > 0)
        {
            list.Add(new WalletTransaction
            {
                id = Guid.NewGuid().ToString(),
                player_id = playerId,
                amount = coins,
                currency = "coins",
                type = type,
                timestamp = timestamp,
                source = safeSource,
                event_id = eventId
            });
        }

        if (gems > 0)
        {
            list.Add(new WalletTransaction
            {
                id = Guid.NewGuid().ToString(),
                player_id = playerId,
                amount = gems,
                currency = "gems",
                type = type,
                timestamp = timestamp,
                source = safeSource,
                event_id = eventId
            });
        }

        if (coachingCredits > 0)
        {
            list.Add(new WalletTransaction
            {
                id = Guid.NewGuid().ToString(),
                player_id = playerId,
                amount = coachingCredits,
                currency = "coaching_credits",
                type = type,
                timestamp = timestamp,
                source = safeSource,
                event_id = eventId
            });
        }

        return list;
    }

    private List<WalletTransaction> LoadTransactions(string playerId)
    {
        var transactionsPath = GetCanonicalEconomyFilePath(playerId, TransactionsFileName);
        var transactions = ReadTransactionsForPlayer(transactionsPath, playerId);
        if (transactions != null)
        {
            MirrorEditorDebugTransactions(transactions);
            return transactions;
        }

        if (TryMigrateLegacyTransactions(playerId, transactionsPath, out var migratedTransactions))
        {
            return migratedTransactions;
        }

        var emptyTransactions = new List<WalletTransaction>();
        MirrorEditorDebugTransactions(emptyTransactions);
        return emptyTransactions;
    }

    private bool TryMigrateLegacyWallet(string playerId, string destinationPath, out Wallet migratedWallet)
    {
        foreach (var legacyPath in GetLegacyEconomyFilePaths(playerId, WalletFileName))
        {
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            var legacyWallet = ReadJsonFile<Wallet>(legacyPath);
            if (legacyWallet == null)
            {
                continue;
            }

            if (IsLegacySharedStreamingPath(legacyPath) && !CanMigrateSharedWallet(legacyWallet, playerId))
            {
                Debug.LogWarning(
                    $"[EconomyService] Skipping shared legacy wallet at '{legacyPath}' because it belongs to '{legacyWallet.player_id}', not '{playerId}'.");
                continue;
            }

            migratedWallet = NormalizeWallet(playerId, legacyWallet);
            WriteJsonAtomic(destinationPath, migratedWallet);
            MirrorEditorDebugWallet(migratedWallet);
            Debug.Log($"[EconomyService] Migrated wallet for '{playerId}' from '{legacyPath}'.");
            return true;
        }

        migratedWallet = null;
        return false;
    }

    private bool TryMigrateLegacyTransactions(
        string playerId,
        string destinationPath,
        out List<WalletTransaction> migratedTransactions)
    {
        foreach (var legacyPath in GetLegacyEconomyFilePaths(playerId, TransactionsFileName))
        {
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            var legacyTransactions = ReadTransactionsForPlayer(legacyPath, playerId);
            if (legacyTransactions == null)
            {
                continue;
            }

            migratedTransactions = legacyTransactions;
            WriteJsonAtomic(destinationPath, migratedTransactions);
            MirrorEditorDebugTransactions(migratedTransactions);
            Debug.Log($"[EconomyService] Migrated wallet transactions for '{playerId}' from '{legacyPath}'.");
            return true;
        }

        migratedTransactions = new List<WalletTransaction>();
        return false;
    }

    private static Wallet ReadWalletForPlayer(string path, string playerId)
    {
        var wallet = ReadJsonFile<Wallet>(path);
        return NormalizeWallet(playerId, wallet);
    }

    private static List<WalletTransaction> ReadTransactionsForPlayer(string path, string playerId)
    {
        var transactions = ReadJsonFile<List<WalletTransaction>>(path);
        if (transactions == null)
        {
            return null;
        }

        return NormalizeTransactions(playerId, transactions);
    }

    private static Wallet NormalizeWallet(string playerId, Wallet wallet)
    {
        if (wallet == null)
        {
            return null;
        }

        wallet.player_id = playerId;
        if (string.IsNullOrWhiteSpace(wallet.last_updated))
        {
            wallet.last_updated = UtcNowIso();
        }

        return wallet;
    }

    private static List<WalletTransaction> NormalizeTransactions(
        string playerId,
        IEnumerable<WalletTransaction> transactions)
    {
        var normalized = new List<WalletTransaction>();
        if (transactions == null)
        {
            return normalized;
        }

        foreach (var transaction in transactions)
        {
            if (transaction == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(transaction.player_id) &&
                !string.Equals(transaction.player_id, playerId, StringComparison.Ordinal))
            {
                continue;
            }

            normalized.Add(NormalizeTransaction(playerId, transaction));
        }

        return normalized;
    }

    private static WalletTransaction NormalizeTransaction(string playerId, WalletTransaction transaction)
    {
        return new WalletTransaction
        {
            id = string.IsNullOrWhiteSpace(transaction.id) ? Guid.NewGuid().ToString() : transaction.id,
            player_id = string.IsNullOrWhiteSpace(transaction.player_id) ? playerId : transaction.player_id,
            amount = transaction.amount,
            currency = transaction.currency,
            type = transaction.type,
            timestamp = string.IsNullOrWhiteSpace(transaction.timestamp) ? UtcNowIso() : transaction.timestamp,
            source = string.IsNullOrWhiteSpace(transaction.source) ? "unknown_source" : transaction.source,
            event_id = transaction.event_id
        };
    }

    private static bool CanMigrateSharedWallet(Wallet wallet, string playerId)
    {
        return string.IsNullOrWhiteSpace(wallet.player_id) ||
               string.Equals(wallet.player_id, playerId, StringComparison.Ordinal);
    }

    private static bool IsLegacySharedStreamingPath(string path)
    {
        return string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(GetLegacyStreamingEconomyPath(Path.GetFileName(path))),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCanonicalEconomyFilePath(string playerId, string fileName)
    {
        return FilePathResolver.GetEconomyPath(playerId, fileName);
    }

    private static IEnumerable<string> GetLegacyEconomyFilePaths(string playerId, string fileName)
    {
        yield return GetLegacyUppercaseEconomyPath(playerId, fileName);
        yield return GetLegacyStreamingEconomyPath(fileName);
    }

    private static string GetLegacyUppercaseEconomyPath(string playerId, string fileName)
    {
        var playerRoot = FilePathResolver.GetPlayerDataRoot(playerId);
        var dir = Path.Combine(playerRoot, LegacyUppercaseEconomyFolderName);
        return Path.Combine(dir, fileName);
    }

    private static string GetLegacyStreamingEconomyPath(string fileName)
    {
        var dir = Path.Combine(Application.dataPath, LegacyStreamingAssetsFolderName, LegacyUppercaseEconomyFolderName);
        return Path.Combine(dir, fileName);
    }

    private static void MirrorEditorDebugWallet(Wallet wallet)
    {
#if UNITY_EDITOR
        if (wallet == null)
        {
            return;
        }

        TryMirrorEditorDebugFile(WalletFileName, wallet);
#endif
    }

    private static void MirrorEditorDebugTransactions(IEnumerable<WalletTransaction> transactions)
    {
#if UNITY_EDITOR
        TryMirrorEditorDebugFile(
            TransactionsFileName,
            transactions?.ToList() ?? new List<WalletTransaction>());
#endif
    }

#if UNITY_EDITOR
    private static void TryMirrorEditorDebugFile<T>(string fileName, T data)
    {
        try
        {
            WriteJsonAtomic(GetLegacyStreamingEconomyPath(fileName), data);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[EconomyService] Failed to mirror '{fileName}' to StreamingAssets for editor debugging: {ex.Message}");
        }
    }
#endif

    private void PublishWalletUpdated(string playerId, string source, Wallet wallet)
    {
        if (!PublishWalletUpdatedEvents)
        {
            return;
        }

        var payload = new WalletUpdatedPayload
        {
            player_id = playerId,
            coins = wallet.coins,
            gems = wallet.gems,
            coaching_credits = wallet.coaching_credits,
            source = string.IsNullOrWhiteSpace(source) ? "unknown_source" : source,
            timestamp = UtcNowIso()
        };

        EventBus.Publish(new EventBus.EventEnvelope
        {
            event_type = "wallet_updated",
            player_id = playerId,
            payloadJson = JsonConvert.SerializeObject(payload)
        });
    }

    private static void TrySynchronizeForecast(string playerId)
    {
        try
        {
            new EconomyForecastService().TrySynchronizeForecastFile(playerId);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EconomyService] Failed to synchronize economy_forecast.json: {ex.Message}");
        }
    }

    private static T ReadJsonFile<T>(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return default;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EconomyService] Failed to read JSON at '{path}': {ex.Message}");
            return default;
        }
    }

    private static void WriteJsonAtomic<T>(string destinationPath, T data)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = destinationPath + ".tmp";
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        File.WriteAllText(tempPath, json);
        if (File.Exists(destinationPath))
        {
            var backupPath = destinationPath + ".bak";
            File.Replace(tempPath, destinationPath, backupPath, true);
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            return;
        }

        File.Move(tempPath, destinationPath);
    }

    private static string UtcNowIso()
    {
        return DateTime.UtcNow.ToString("o");
    }

    private static DateTime ParseTimestampOrMin(string timestamp)
    {
        return DateTime.TryParse(timestamp, out var parsed) ? parsed : DateTime.MinValue;
    }
}

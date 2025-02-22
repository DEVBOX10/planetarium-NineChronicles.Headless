using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Cocona;
using Cocona.Help;
using Libplanet;
using Libplanet.Action;
using Libplanet.Blockchain;
using Libplanet.Blockchain.Policies;
using Libplanet.Blocks;
using Libplanet.Extensions.Cocona;
using Libplanet.RocksDBStore;
using Libplanet.Store;
using Libplanet.Store.Trie;
using Nekoyume.Action;
using Nekoyume.BlockChain.Policy;
using NineChronicles.Headless.Executable.IO;
using NineChronicles.Headless.Executable.Store;
using Serilog.Core;
using NCAction = Libplanet.Action.PolymorphicAction<Nekoyume.Action.ActionBase>;

namespace NineChronicles.Headless.Executable.Commands
{
    public class ChainCommand : CoconaLiteConsoleAppBase
    {
        private readonly IConsole _console;

        public ChainCommand(IConsole console)
        {
            _console = console;
        }

        [PrimaryCommand]
        public void Help([FromService] ICoconaHelpMessageBuilder helpMessageBuilder)
        {
            _console.Out.WriteLine(helpMessageBuilder.BuildAndRenderForCurrentContext());
        }

        [Command(Description = "Print the tip's header of the chain placed at given store path.")]
        public void Tip(
            [Argument("STORE-TYPE",
                Description = "The storage type to store blockchain data. " +
                              "You cannot use \"Memory\" because it's volatile.")]
            StoreType storeType,
            [Argument("STORE-PATH")] string storePath)
        {
            if (storeType == StoreType.Memory)
            {
                throw new CommandExitedException("Memory is volatile. " +
                                                 "Please use persistent StoreType like RocksDb.", -1);
            }

            if (!Directory.Exists(storePath))
            {
                throw new CommandExitedException($"The given STORE-PATH, {storePath} seems not existed.", -1);
            }

            IStagePolicy<NCAction> stagePolicy = new VolatileStagePolicy<PolymorphicAction<ActionBase>>();
            IBlockPolicy<NCAction> blockPolicy = new BlockPolicySource(Logger.None).GetPolicy();
            IStore store = storeType.CreateStore(storePath);
            if (!(store.GetCanonicalChainId() is { } chainId))
            {
                throw new CommandExitedException(
                    $"There is no canonical chain: {storePath}",
                    -1);
            }

            BlockHash tipHash = store.IndexBlockHash(chainId, -1)
                          ?? throw new CommandExitedException("The given chain seems empty.", -1);
            Block<NCAction> tip = store.GetBlock<NCAction>(tipHash);
            _console.Out.WriteLine(Utils.SerializeHumanReadable(tip.Header));
            (store as IDisposable)?.Dispose();
        }

        [Command(Description = "Print each block's mining time and tx stats (total tx, hack and slash, ranking battle, " +
                               "mimisbrunnr) of a given chain in csv format.")]
        public void Inspect(
            [Argument("STORE-TYPE",
                Description = "The storage type to store blockchain data. " +
                              "You cannot use \"Memory\" because it's volatile.")]
            StoreType storeType,
            [Argument("STORE-PATH",
                Description = "Store path to inspect.")]
            string storePath,
            [Argument("OFFSET",
                Description = "Offset of block index.")]
            int? offset = null,
            [Argument("LIMIT",
                Description = "Limit of block count.")]
            int? limit = null)
        {
            if (storeType == StoreType.Memory)
            {
                throw new CommandExitedException("Memory is volatile. " +
                                                 "Please use persistent StoreType like RocksDb.", -1);
            }

            if (!Directory.Exists(storePath))
            {
                throw new CommandExitedException($"The given STORE-PATH, {storePath} seems not existed.", -1);
            }

            IStagePolicy<NCAction> stagePolicy = new VolatileStagePolicy<PolymorphicAction<ActionBase>>();
            IBlockPolicy<NCAction> blockPolicy = new BlockPolicySource(Logger.None).GetPolicy();
            IStore store = storeType.CreateStore(storePath);
            var stateStore = new TrieStateStore(new DefaultKeyValueStore(null));
            if (!(store.GetCanonicalChainId() is { } chainId))
            {
                throw new CommandExitedException($"There is no canonical chain: {storePath}", -1);
            }

            if (!(store.IndexBlockHash(chainId, 0) is { } gHash))
            {
                throw new CommandExitedException($"There is no genesis block: {storePath}", -1);
            }

            Block<NCAction> genesisBlock = store.GetBlock<NCAction>(gHash);
            BlockChain<NCAction> chain = new BlockChain<NCAction>(
                blockPolicy,
                stagePolicy,
                store,
                stateStore,
                genesisBlock);

            long height = chain.Tip.Index;
            if (offset + limit > (int)height)
            {
                throw new CommandExitedException(
                    $"The sum of the offset and limit is greater than the chain tip index: {height}",
                    -1);
            }

            _console.Out.WriteLine("Block Index," +
                                   "Mining Time (sec)," +
                                   "Total Tx #," +
                                   "HAS #," +
                                   "RankingBattle #," +
                                   "Mimisbrunnr #");

            var typeOfActionTypeAttribute = typeof(ActionTypeAttribute);
            foreach (var item in
                store.IterateIndexes(chain.Id, offset + 1 ?? 1, limit).Select((value, i) => new { i, value }))
            {
                var block = store.GetBlock<NCAction>(item.value);
                var previousBlock = store.GetBlock<NCAction>(
                    block.PreviousHash ?? block.Hash
                );

                var miningTime = block.Timestamp - previousBlock.Timestamp;
                var txCount = 0;
                var hackAndSlashCount = 0;
                var rankingBattleCount = 0;
                var mimisbrunnrBattleCount = 0;
                foreach (var tx in block.Transactions)
                {
                    txCount++;
                    foreach (var action in tx.CustomActions!)
                    {
                        var actionTypeAttribute =
                            Attribute.GetCustomAttribute(action.InnerAction.GetType(), typeOfActionTypeAttribute)
                                as ActionTypeAttribute;
                        if (actionTypeAttribute is null)
                        {
                            continue;
                        }

                        var typeIdentifier = actionTypeAttribute.TypeIdentifier;
                        if (typeIdentifier.StartsWith("hack_and_slash"))
                        {
                            hackAndSlashCount++;
                        }
                        else if (typeIdentifier.StartsWith("ranking_battle"))
                        {
                            rankingBattleCount++;
                        }
                        else if (typeIdentifier.StartsWith("mimisbrunnr_battle"))
                        {
                            mimisbrunnrBattleCount++;
                        }
                    }
                }

                _console.Out.WriteLine($"{block.Index}," +
                                       $"{miningTime:s\\.ff}," +
                                       $"{txCount}," +
                                       $"{hackAndSlashCount}," +
                                       $"{rankingBattleCount}," +
                                       $"{mimisbrunnrBattleCount}");
            }

            (store as IDisposable)?.Dispose();
        }

        [Command(Description = "Truncate the chain from the tip by the input value (in blocks)")]
        public void Truncate(
            [Argument("STORE-TYPE",
                Description = "The storage type to store blockchain data. " +
                              "You cannot use \"Memory\" because it's volatile.")]
            StoreType storeType,
            [Argument("STORE-PATH",
                Description = "Store path to inspect.")]
            string storePath,
            [Argument("BLOCKS-BEFORE",
                Description = "Number of blocks to truncate from the tip")]
            int blocksBefore)
        {
            if (storeType == StoreType.Memory)
            {
                throw new CommandExitedException("Memory is volatile. " +
                                                 "Please use persistent StoreType like RocksDb.", -1);
            }

            if (!Directory.Exists(storePath))
            {
                throw new CommandExitedException(
                    $"The given STORE-PATH, {storePath} seems not existed.",
                    -1);
            }

            IStore store = storeType.CreateStore(storePath);
            var statesPath = Path.Combine(storePath, "states");
            IKeyValueStore stateKeyValueStore = new RocksDBKeyValueStore(statesPath);
            var stateStore = new TrieStateStore(stateKeyValueStore);
            if (!(store.GetCanonicalChainId() is { } chainId))
            {
                throw new CommandExitedException(
                    $"There is no canonical chain: {storePath}",
                    -1);
            }

            if (!(store.IndexBlockHash(chainId, 0) is { }))
            {
                throw new CommandExitedException(
                    $"There is no genesis block: {storePath}",
                    -1);
            }

            var tipHash = store.IndexBlockHash(chainId, -1)
                          ?? throw new CommandExitedException("The given chain seems empty.", -1);
            if (!(store.GetBlockIndex(tipHash) is { } tipIndex))
            {
                throw new CommandExitedException(
                    $"The index of {tipHash} doesn't exist.",
                    -1);
            }

            var tip = store.GetBlock<NCAction>(tipHash);
            var snapshotTipIndex = Math.Max(tipIndex - (blocksBefore + 1), 0);
            BlockHash snapshotTipHash;

            do
            {
                snapshotTipIndex++;
                _console.Out.WriteLine(snapshotTipIndex);
                if (!(store.IndexBlockHash(chainId, snapshotTipIndex) is { } hash))
                {
                    throw new CommandExitedException(
                        $"The index {snapshotTipIndex} doesn't exist on ${chainId}.",
                        -1);
                }

                snapshotTipHash = hash;
            } while (!stateStore.ContainsStateRoot(store.GetBlock<NCAction>(snapshotTipHash).StateRootHash));

            var forkedId = Guid.NewGuid();

            Fork(chainId, forkedId, snapshotTipHash, tip, store);

            store.SetCanonicalChainId(forkedId);
            foreach (var id in store.ListChainIds().Where(id => !id.Equals(forkedId)))
            {
                store.DeleteChainId(id);
            }

            store.Dispose();
            stateStore.Dispose();
        }

        [Command(Description = "Prune states in the chain")]
        public void PruneStates(
            [Argument("STORE-TYPE",
                Description = "Store type of RocksDb.")]
            StoreType storeType,
            [Argument("STORE-PATH",
                Description = "Store path to prune states.")]
            string storePath)
        {
            if (!Directory.Exists(storePath))
            {
                throw new CommandExitedException(
                    $"The given STORE-PATH, {storePath} seems not existed.",
                    -1);
            }

            IStore store = storeType.CreateStore(storePath);
            var statesPath = Path.Combine(storePath, "states");
            IKeyValueStore stateKeyValueStore = new RocksDBKeyValueStore(statesPath);
            var stateStore = new TrieStateStore(stateKeyValueStore);
            if (!(store.GetCanonicalChainId() is { } chainId))
            {
                throw new CommandExitedException(
                    $"There is no canonical chain: {storePath}",
                    -1);
            }

            if (!(store.IndexBlockHash(chainId, 0) is { }))
            {
                throw new CommandExitedException(
                    $"There is no genesis block: {storePath}",
                    -1);
            }

            var tipHash = store.IndexBlockHash(chainId, -1)
                          ?? throw new CommandExitedException("The given chain seems empty.", -1);

            if (!(store.GetBlockIndex(tipHash) is { }))
            {
                throw new CommandExitedException(
                    $"The index of {tipHash} doesn't exist.",
                    -1);
            }

            var newStatesPath = Path.Combine(storePath, "new_states");
            IKeyValueStore newStateKeyValueStore = new RocksDBKeyValueStore(newStatesPath);
            var newStateStore = new TrieStateStore(newStateKeyValueStore);
            if (!(store.GetStateRootHash(tipHash) is { } snapshotTipStateRootHash))
            {
                throw new CommandExitedException(
                    $"The StateRootHash of {tipHash} doesn't exist.",
                    -1);
            }

            _console.Out.WriteLine("Counting keys in states store.");
            var totalKeyCount = stateKeyValueStore.ListKeys().Count();
            _console.Out.WriteLine($"Pruning States Start. Total Number of State Keys: {totalKeyCount}");
            var start = DateTimeOffset.Now;
            stateStore.CopyStates(ImmutableHashSet<HashDigest<SHA256>>.Empty
                .Add(snapshotTipStateRootHash), newStateStore);
            var prunedKeyCount = totalKeyCount - newStateKeyValueStore.ListKeys().Count();
            var end = DateTimeOffset.Now;
            _console.Out.WriteLine($"Pruning States Done. Pruned {prunedKeyCount} out of {totalKeyCount} keys.Time Taken: {end - start:g}");
            store.Dispose();
            stateStore.Dispose();
            newStateStore.Dispose();
            Directory.Delete(statesPath, true);
            Directory.Move(newStatesPath, statesPath);
        }

        private void Fork(
            Guid src,
            Guid dest,
            BlockHash branchPointHash,
            Block<NCAction> tip,
            IStore store)
        {
            store.ForkBlockIndexes(src, dest, branchPointHash);
            store.ForkTxNonces(src, dest);

            for (
                Block<NCAction> block = tip;
                block.PreviousHash is { } hash
                && !block.Hash.Equals(branchPointHash);
                block = store.GetBlock<NCAction>(hash))
            {
                IEnumerable<(Address, int)> signers = block
                    .Transactions
                    .GroupBy(tx => tx.Signer)
                    .Select(g => (g.Key, g.Count()));

                foreach ((Address address, int txCount) in signers)
                {
                    store.IncreaseTxNonce(dest, address, -txCount);
                }
            }
        }
    }
}

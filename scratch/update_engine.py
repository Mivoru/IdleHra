import re

with open(r'c:\Users\promi\skola2025\IdleHra\server\FolkIdle.Server\Engine\GuildContributionEngine.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_code = """        public async Task<bool> ContributeDepotMaterialAsync(long playerId, long guildId, string itemId, int quantity)
        {
            if (quantity <= 0 || itemId == "gold") return false; // Gold handled separately if needed, but per plan no gold for buffs."""

new_code = """        public async Task<bool> ContributeDepotMaterialAsync(long playerId, long guildId, string itemId, int quantity)
        {
            if (quantity <= 0) return false;

            if (itemId == "gold")
            {
                using var scope_gold = _serviceProvider.CreateScope();
                var db_gold = scope_gold.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                using var transaction_gold = await db_gold.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    var playerQuery = "SELECT * FROM \\"PlayerRecords\\" WHERE \\"Id\\" = {0} FOR UPDATE";
                    var player = await db_gold.PlayerRecords.FromSqlRaw(playerQuery, playerId).SingleOrDefaultAsync();
                    if (player == null || player.Gold < quantity) return false;

                    var guildQuery = "SELECT * FROM \\"GuildRecords\\" WHERE \\"Id\\" = {0} FOR UPDATE";
                    var guild = await db_gold.GuildRecords.FromSqlRaw(guildQuery, guildId).SingleOrDefaultAsync();
                    if (guild == null) return false;

                    player.Gold -= quantity;
                    guild.TotalGoldContributed += quantity;

                    int points = quantity / 10000;
                    if (points < 1) points = 1;

                    await db_gold.Database.ExecuteSqlRawAsync(
                        "UPDATE \\"GuildMembers\\" SET \\"WeeklyContributionPoints\\" = \\"WeeklyContributionPoints\\" + {0} WHERE \\"GuildId\\" = {1} AND \\"PlayerId\\" = {2}",
                        points, guildId, playerId);

                    await db_gold.SaveChangesAsync();
                    await transaction_gold.CommitAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                    return true;
                }
                catch (System.Exception ex)
                {
                    await transaction_gold.RollbackAsync();
                    System.Console.WriteLine($"Depot gold contribution failed: {ex.Message}");
                    return false;
                }
            }"""

content = content.replace(old_code, new_code)

with open(r'c:\Users\promi\skola2025\IdleHra\server\FolkIdle.Server\Engine\GuildContributionEngine.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("done")

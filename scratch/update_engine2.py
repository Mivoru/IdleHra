import re

with open(r'c:\Users\promi\skola2025\IdleHra\server\FolkIdle.Server\Engine\GuildContributionEngine.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_code = """        public async Task<bool> ContributeDepotMaterialAsync(long playerId, long guildId, string itemId, int quantity)
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
                    if (player == null || player.Gold < quantity) return false;"""

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
                    var recordQuery = "SELECT * FROM \\"CommodityRecords\\" WHERE \\"PlayerId\\" = {0} AND \\"ItemId\\" = {1} FOR UPDATE";
                    var playerCommodity = await db_gold.CommodityRecords.FromSqlRaw(recordQuery, playerId, itemId).SingleOrDefaultAsync();
                    if (playerCommodity == null || playerCommodity.Quantity < quantity) return false;
                    
                    playerCommodity.Quantity -= quantity;
                    if (playerCommodity.Quantity <= 0)
                    {
                        db_gold.CommodityRecords.Remove(playerCommodity);
                    }"""

content = content.replace(old_code, new_code)
content = content.replace("player.Gold -= quantity;", "// player.Gold handled by playerCommodity")

with open(r'c:\Users\promi\skola2025\IdleHra\server\FolkIdle.Server\Engine\GuildContributionEngine.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("done")

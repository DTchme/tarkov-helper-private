using System.Text.Json.Serialization;

namespace TarkovHelper.Models
{
    /// <summary>
    /// Represents user's inventory quantity for an item with FIR/Non-FIR separation
    /// </summary>
    public class ItemInventory
    {
        /// <summary>
        /// Item normalized name (key for lookup)
        /// </summary>
        [JsonPropertyName("itemNormalizedName")]
        public string ItemNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Found in Raid quantity
        /// </summary>
        [JsonPropertyName("firQuantity")]
        public int FirQuantity { get; set; }

        /// <summary>
        /// Non-FIR quantity (purchased from flea market, etc.)
        /// </summary>
        [JsonPropertyName("nonFirQuantity")]
        public int NonFirQuantity { get; set; }

        /// <summary>
        /// Total quantity (FIR + Non-FIR)
        /// </summary>
        [JsonIgnore]
        public int TotalQuantity => FirQuantity + NonFirQuantity;
    }

    /// <summary>
    /// Container for all item inventory data (for JSON serialization)
    /// </summary>
    public class ItemInventoryData
    {
        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("items")]
        public Dictionary<string, ItemInventory> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fulfillment status for an item requirement
    /// </summary>
    public enum ItemFulfillmentStatus
    {
        /// <summary>
        /// No items owned (0/required)
        /// </summary>
        NotStarted,

        /// <summary>
        /// Some items owned but not enough
        /// </summary>
        PartiallyFulfilled,

        /// <summary>
        /// All requirements met
        /// </summary>
        Fulfilled
    }

    /// <summary>
    /// Detailed fulfillment information for an item
    /// </summary>
    public class ItemFulfillmentInfo
    {
        /// <summary>
        /// Item normalized name
        /// </summary>
        public string ItemNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Total required quantity
        /// </summary>
        public int RequiredTotal { get; set; }

        /// <summary>
        /// Required FIR quantity (if FIR is required)
        /// </summary>
        public int RequiredFir { get; set; }

        /// <summary>
        /// User's FIR quantity owned
        /// </summary>
        public int OwnedFir { get; set; }

        /// <summary>
        /// User's Non-FIR quantity owned
        /// </summary>
        public int OwnedNonFir { get; set; }

        /// <summary>
        /// Total owned (FIR + Non-FIR)
        /// </summary>
        public int OwnedTotal => OwnedFir + OwnedNonFir;

        /// <summary>
        /// Whether FIR requirement is met
        /// </summary>
        public bool IsFirFulfilled => OwnedFir >= RequiredFir;

        /// <summary>
        /// Whether total requirement is met
        /// </summary>
        public bool IsTotalFulfilled => OwnedTotal >= RequiredTotal;

        /// <summary>
        /// Overall fulfillment status
        /// </summary>
        public ItemFulfillmentStatus Status
        {
            get
            {
                if (RequiredFir > 0)
                {
                    // If FIR is required, check FIR quantity
                    if (OwnedFir >= RequiredFir)
                        return ItemFulfillmentStatus.Fulfilled;
                    if (OwnedFir > 0 || OwnedNonFir > 0)
                        return ItemFulfillmentStatus.PartiallyFulfilled;
                    return ItemFulfillmentStatus.NotStarted;
                }
                else
                {
                    // Non-FIR OK, check total quantity
                    if (OwnedTotal >= RequiredTotal)
                        return ItemFulfillmentStatus.Fulfilled;
                    if (OwnedTotal > 0)
                        return ItemFulfillmentStatus.PartiallyFulfilled;
                    return ItemFulfillmentStatus.NotStarted;
                }
            }
        }

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        public double ProgressPercent
        {
            get
            {
                if (RequiredTotal == 0) return 100;

                if (RequiredFir > 0)
                {
                    // For FIR items, calculate based on FIR quantity only
                    return Math.Min(100, (double)OwnedFir / RequiredFir * 100);
                }
                else
                {
                    // For non-FIR, calculate based on total
                    return Math.Min(100, (double)OwnedTotal / RequiredTotal * 100);
                }
            }
        }
    }
}

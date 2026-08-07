namespace TarkovHelper.Models
{
    /// <summary>
    /// Quest completion status
    /// </summary>
    public enum QuestStatus
    {
        /// <summary>
        /// Cannot be activated - prerequisites not met
        /// </summary>
        Locked,

        /// <summary>
        /// Available to start/in progress
        /// </summary>
        Active,

        /// <summary>
        /// Completed successfully
        /// </summary>
        Done,

        /// <summary>
        /// Failed (user marked as failed)
        /// </summary>
        Failed,

        /// <summary>
        /// Prerequisites met but player level too low
        /// </summary>
        LevelLocked,

        /// <summary>
        /// Quest is not available due to edition or prestige level requirements
        /// </summary>
        Unavailable
    }
}

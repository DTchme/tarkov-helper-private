# Load SQLite assembly from build output
$dllPath = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows\Microsoft.Data.Sqlite.dll"
$sqlitePCLPath = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows\SQLitePCLRaw.core.dll"
$sqliteBundlePath = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows\SQLitePCLRaw.bundle_e_sqlite3.dll"

Add-Type -Path $dllPath
Add-Type -Path $sqlitePCLPath

# Initialize SQLitePCL
[SQLitePCL.Batteries_V2]::Init()

$dbPath = Join-Path $PSScriptRoot "Assets\tarkov_data.db"
$connString = "Data Source=$dbPath;Mode=ReadOnly"

$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection($connString)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT q.Name as QuestName, rq.Name as RequiredQuest, qr.GroupId, qr.RequirementType
FROM QuestRequirements qr
JOIN Quests q ON qr.QuestId = q.Id
JOIN Quests rq ON qr.RequiredQuestId = rq.Id
WHERE q.Name LIKE '%Woods Keeper%'
ORDER BY qr.GroupId
"@

$reader = $cmd.ExecuteReader()
Write-Host "`nWoods Keeper Quest Requirements:"
Write-Host "================================"
while ($reader.Read()) {
    $questName = $reader.GetString(0)
    $reqName = $reader.GetString(1)
    $groupId = $reader.GetInt32(2)
    $reqType = if ($reader.IsDBNull(3)) { "Complete" } else { $reader.GetString(3) }
    Write-Host "  Required: $reqName | GroupId=$groupId | Type=$reqType"
}
$reader.Close()

# Also check for any OR groups in the DB
$cmd2 = $conn.CreateCommand()
$cmd2.CommandText = @"
SELECT COUNT(*) FROM QuestRequirements WHERE GroupId > 0
"@
$orCount = $cmd2.ExecuteScalar()
Write-Host "`nTotal OR group requirements in DB: $orCount"

$conn.Close()

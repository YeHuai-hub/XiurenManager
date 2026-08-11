import fs from "node:fs";
import path from "node:path";

const [, , backupPath, ledgerPath, outputPath, ...queueTargets] = process.argv;
if (!backupPath || !ledgerPath || !outputPath) {
  throw new Error("Usage: node recover-database-from-ledger.mjs <backup> <ledger> <output> [queue targets...]");
}

const readJson = (file) => JSON.parse(fs.readFileSync(file, "utf8").replace(/^\uFEFF/, ""));
const database = readJson(backupPath);
const ledger = readJson(ledgerPath);
if (!Array.isArray(database.Resources) || !Array.isArray(database.Jobs) || !Array.isArray(ledger.Items)) {
  throw new Error("Recovery inputs do not use the expected Xiuren database and ledger schemas.");
}

const byPostId = new Map();
const byUrl = new Map();
const byDirectory = new Map();
const remember = (resource) => {
  if (resource.PostId) byPostId.set(String(resource.PostId), resource);
  if (resource.DetailUrl) byUrl.set(String(resource.DetailUrl).toLowerCase(), resource);
  if (resource.LocalDir) byDirectory.set(path.resolve(resource.LocalDir).toLowerCase(), resource);
};
database.Resources.forEach(remember);

let addedResources = 0;
let refreshedResources = 0;
for (const item of ledger.Items) {
  const existing =
    (item.SourcePostId && byPostId.get(String(item.SourcePostId))) ||
    (item.SourceUrl && byUrl.get(String(item.SourceUrl).toLowerCase())) ||
    (item.LocalDir && byDirectory.get(path.resolve(item.LocalDir).toLowerCase()));
  const locallyPresent = !["Missing", "Offline", "Deleted"].includes(item.Availability);
  if (existing) {
    existing.Model = item.Model || existing.Model;
    existing.Category = item.Category || existing.Category;
    existing.DetectedCategory = item.Category || existing.DetectedCategory;
    existing.DetailUrl = item.SourceUrl || existing.DetailUrl;
    existing.PanUrl = item.PanUrl || existing.PanUrl;
    existing.PanPassword = item.PanPassword || existing.PanPassword;
    existing.ExtractPassword = item.ExtractPassword || existing.ExtractPassword;
    existing.LocalDir = item.LocalDir || existing.LocalDir;
    existing.LastChecked = item.LastVerified || item.LastScanned || existing.LastChecked;
    if (locallyPresent && item.LocalDir) {
      existing.DownloadStatus = "Downloaded";
      existing.ExtractStatus = "Extracted";
      existing.Error = "";
    }
    refreshedResources++;
    continue;
  }

  const recovered = {
    PostId: item.SourcePostId || "",
    Title: item.Title || "",
    Model: item.Model || "unknown",
    Category: item.Category || "秀人",
    CategorySource: "RecoveredLedger",
    DetectedCategory: item.Category || "秀人",
    DetailUrl: item.SourceUrl || "",
    PanUrl: item.PanUrl || "",
    PanPassword: item.PanPassword || "",
    ExtractPassword: item.ExtractPassword || "",
    ResourceType: Number(item.VideoCount || 0) > 0 && Number(item.ImageCount || 0) === 0 ? "Video" : "Photo",
    Status: "Ready",
    DownloadStatus: locallyPresent ? "Downloaded" : "Pending",
    ExtractStatus: locallyPresent ? "Extracted" : "Pending",
    LocalDir: item.LocalDir || "",
    Error: locallyPresent ? "" : (item.AvailabilityReason || "Recovered from library ledger"),
    LastChecked: item.LastVerified || item.LastScanned || ledger.UpdatedAt || ""
  };
  database.Resources.push(recovered);
  remember(recovered);
  addedResources++;
}

database.LocalFiles = ledger.Items;
const completedHistory = database.Jobs.filter((job) => job.Status === "Done");
const recoveredJobs = queueTargets.slice().reverse().map((target) => ({
  Type: "SearchDownload",
  Target: target,
  Aliases: "",
  Exclusions: "",
  Pages: 999,
  MaxReady: 9999,
  SearchMode: "Global",
  CategoryPath: "",
  DownloadCategory: "COS",
  Status: "Queued",
  Stage: "",
  ProgressTotal: 0,
  ProgressCompleted: 0,
  ProgressSkipped: 0,
  ProgressFailed: 0,
  ProgressDeferred: 0,
  Error: "Recovered after unexpected Windows restart",
  StartedAt: new Date().toISOString().slice(0, 19),
  FinishedAt: ""
}));
database.Jobs = [...recoveredJobs, ...completedHistory];

const outputDirectory = path.dirname(outputPath);
const backupDirectory = path.join(outputDirectory, "backups");
fs.mkdirSync(backupDirectory, { recursive: true });
const stamp = new Date().toISOString().replace(/[-:]/g, "").replace(/\..*$/, "").replace("T", "-");
const damagedCopy = path.join(backupDirectory, `xiuren.db.zero-filled-${stamp}`);
if (fs.existsSync(outputPath)) fs.renameSync(outputPath, damagedCopy);

const temporary = outputPath + ".recovery.tmp";
const payload = JSON.stringify(database, null, 2);
fs.writeFileSync(temporary, payload, "utf8");
const handle = fs.openSync(temporary, "r+");
try {
  fs.fsyncSync(handle);
} finally {
  fs.closeSync(handle);
}
JSON.parse(fs.readFileSync(temporary, "utf8"));
fs.renameSync(temporary, outputPath);

console.log(JSON.stringify({
  resources: database.Resources.length,
  localFiles: database.LocalFiles.length,
  jobs: database.Jobs.length,
  addedResources,
  refreshedResources,
  damagedCopy,
  queueTargets
}, null, 2));

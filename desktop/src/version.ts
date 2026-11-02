export interface BuildInfo {
  version: string;
  commit: string;
  commitShort: string;
  commitTime: string;
  buildTime: string;
  dirty: boolean;
}

export interface ReleaseSection {
  title: string;
  items: string[];
}

export interface ReleaseNote {
  version: string;
  date: string | null;
  sections: ReleaseSection[];
}

export interface AppInfo {
  build: BuildInfo;
  releases: ReleaseNote[];
  repository: string;
  releasesUrl: string;
  updateApi: string;
}

export interface ReleaseAsset {
  name: string;
  browser_download_url: string;
  size: number;
}

export interface LatestRelease {
  tag_name: string;
  name: string | null;
  body: string | null;
  html_url: string;
  published_at: string | null;
  prerelease: boolean;
  draft: boolean;
  assets: ReleaseAsset[];
}

export interface UpdateCheck {
  latest: LatestRelease;
  latestVersion: string;
  available: boolean;
  downloadUrl: string;
  downloadLabel: string;
  releaseUrl: string;
  checkedAt: number;
}

interface ParsedVersion {
  core: [bigint, bigint, bigint];
  prerelease: string[];
}

const VERSION_PATTERN = /^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/;

export function normalizeVersion(value: string): string {
  return value.trim().replace(/^[vV]/, '');
}

export function parseVersion(value: string): ParsedVersion | null {
  const match = VERSION_PATTERN.exec(normalizeVersion(value));
  if (!match) return null;
  const prerelease = match[4] ? match[4].split('.') : [];
  if (prerelease.some(identifier => identifier.length === 0)) return null;
  return {
    core: [BigInt(match[1]), BigInt(match[2]), BigInt(match[3])],
    prerelease,
  };
}

/** Semantic-version ordering; build metadata is ignored. Throws on malformed input. */
export function compareVersions(left: string, right: string): number {
  const first = parseVersion(left);
  const second = parseVersion(right);
  if (!first || !second) throw new Error(`无法比较版本号：${left} / ${right}`);
  for (let index = 0; index < 3; index += 1) {
    if (first.core[index] !== second.core[index]) return first.core[index] > second.core[index] ? 1 : -1;
  }
  if (!first.prerelease.length && !second.prerelease.length) return 0;
  if (!first.prerelease.length) return 1;
  if (!second.prerelease.length) return -1;
  const length = Math.max(first.prerelease.length, second.prerelease.length);
  for (let index = 0; index < length; index += 1) {
    const a = first.prerelease[index];
    const b = second.prerelease[index];
    if (a === undefined) return -1;
    if (b === undefined) return 1;
    const result = compareIdentifier(a, b);
    if (result !== 0) return result;
  }
  return 0;
}

function compareIdentifier(left: string, right: string): number {
  const leftNumeric = /^\d+$/.test(left);
  const rightNumeric = /^\d+$/.test(right);
  if (leftNumeric && rightNumeric) {
    const a = BigInt(left);
    const b = BigInt(right);
    return a === b ? 0 : a > b ? 1 : -1;
  }
  if (leftNumeric) return -1;
  if (rightNumeric) return 1;
  return left === right ? 0 : left > right ? 1 : -1;
}

export function isNewerVersion(candidate: string, current: string): boolean {
  try {
    return compareVersions(candidate, current) > 0;
  } catch {
    return false;
  }
}

export function selectDownload(release: LatestRelease): { url: string; label: string } {
  const assets = release.assets ?? [];
  const installer = assets.find(asset => /\.exe$/i.test(asset.name) && /(setup|installer)/i.test(asset.name))
    ?? assets.find(asset => /\.exe$/i.test(asset.name));
  if (installer) return { url: installer.browser_download_url, label: '下载安装程序' };
  const portable = assets.find(asset => /\.zip$/i.test(asset.name));
  if (portable) return { url: portable.browser_download_url, label: '下载便携包' };
  return { url: release.html_url, label: '打开发布页面' };
}

export async function fetchLatestRelease(endpoint: string, request: typeof fetch = fetch): Promise<LatestRelease> {
  let response: Response;
  try {
    response = await request(endpoint, { headers: { Accept: 'application/vnd.github+json' }, cache: 'no-store' });
  } catch {
    throw new Error('无法连接更新服务，请检查网络或稍后重试。');
  }
  if (!response.ok) {
    if (response.status === 403 || response.status === 429) throw new Error('GitHub 暂时限制了更新检查，请稍后再试。');
    throw new Error(`更新服务返回 HTTP ${response.status}。`);
  }
  const payload = await response.json().catch(() => null) as Partial<LatestRelease> | null;
  if (!payload || typeof payload.tag_name !== 'string' || typeof payload.html_url !== 'string') {
    throw new Error('更新服务返回了无法识别的数据。');
  }
  return {
    tag_name: payload.tag_name,
    name: typeof payload.name === 'string' ? payload.name : null,
    body: typeof payload.body === 'string' ? payload.body : null,
    html_url: payload.html_url,
    published_at: typeof payload.published_at === 'string' ? payload.published_at : null,
    prerelease: payload.prerelease === true,
    draft: payload.draft === true,
    assets: Array.isArray(payload.assets) ? payload.assets.filter(asset => asset && typeof asset.name === 'string' && typeof asset.browser_download_url === 'string').map(asset => ({ name: asset.name, browser_download_url: asset.browser_download_url, size: Number(asset.size) || 0 })) : [],
  };
}

export function toUpdateCheck(latest: LatestRelease, currentVersion: string): UpdateCheck {
  const latestVersion = normalizeVersion(latest.tag_name);
  const download = selectDownload(latest);
  return {
    latest,
    latestVersion,
    available: !latest.draft && !latest.prerelease && isNewerVersion(latestVersion, currentVersion),
    downloadUrl: download.url,
    downloadLabel: download.label,
    releaseUrl: latest.html_url,
    checkedAt: Date.now(),
  };
}

export function formatStamp(value: string | null | undefined): string {
  if (!value || value === 'unknown') return '未知';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toLocaleString('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false });
}

export function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return '';
  if (value >= 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} MB`;
  if (value >= 1024) return `${Math.round(value / 1024)} KB`;
  return `${value} B`;
}

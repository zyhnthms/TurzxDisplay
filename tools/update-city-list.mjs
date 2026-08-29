#!/usr/bin/env node
/**
 * 生成 Data/china-cities.tsv：
 * 从 qwd/LocationList 下载 China-City-List-latest.csv（约 3600 个中国城市/区县），
 * 精简为「Adm1 \t Adm2 \t 名称 \t 纬度 \t 经度」五行制 TSV。
 *
 * 用法：node tools/update-city-list.mjs   （默认经 gh-proxy 代理，国内可直连）
 *       node tools/update-city-list.mjs --direct   （直连 raw.githubusercontent.com）
 */
import fs from 'node:fs';
import path from 'node:path';

const direct = process.argv.includes('--direct');
const url = (direct ? '' : 'https://gh-proxy.com/') +
    'https://raw.githubusercontent.com/qwd/LocationList/master/China-City-List-latest.csv';

const outDir = path.resolve(import.meta.dirname, '..', 'Data');
const outFile = path.join(outDir, 'china-cities.tsv');

console.log('downloading', url);
const csv = await (await fetch(url)).text();

const rows = [];
for (const line of csv.split(/\r?\n/)) {
    const c = line.split(',');
    if (c.length < 14 || !/^[0-9A-F]+$/i.test(c[0])) continue;   // 版本行/表头/异常行
    const [, , name, , , , , adm1, , adm2, , lat, lon] = c;
    if (!name || !adm1 || !adm2 || !lat || !lon) continue;
    rows.push([adm1, adm2, name, lat, lon].join('\t'));
}

fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(outFile, rows.join('\n') + '\n');
console.log(`ok: ${rows.length} rows -> ${outFile}`);

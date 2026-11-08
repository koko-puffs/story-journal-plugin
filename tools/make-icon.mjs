// Renders tools/icon.svg to StoryJournal/images/icon.png (512×512, what Dalamud's installer shows),
// with Playwright's Chromium (require('playwright') from anywhere it's installed).
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_PATH ?? 'playwright')

const svg = readFileSync(join(here, 'icon.svg'), 'utf8')
const browser = await chromium.launch()
const page = await browser.newPage({ viewport: { width: 512, height: 512 } })
await page.setContent(`<html><body style="margin:0;background:transparent">${svg.replace('<svg ', '<svg width="512" height="512" ')}</body></html>`)
await page.screenshot({ path: join(here, '..', 'StoryJournal', 'images', 'icon.png'), omitBackground: true })
await browser.close()
console.log('Wrote StoryJournal/images/icon.png')

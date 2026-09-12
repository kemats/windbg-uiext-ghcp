import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const web = join(root, 'web')
const output = resolve(process.argv[2] ?? join(root, 'artifacts', 'web-licenses'))
const lock = JSON.parse(readFileSync(join(web, 'package-lock.json'), 'utf8'))
const fallback = new Map([
  ['fastdom', join(root, 'third-party-licenses', 'npm-fallback', 'fastdom-LICENSE.txt')],
  ['strictdom', join(root, 'third-party-licenses', 'npm-fallback', 'strictdom-LICENSE.txt')],
])
const licenseOverrides = new Map([['khroma', 'MIT']])
const allowedLicenses = new Set([
  'MIT', 'ISC', 'BSD-2-Clause', 'BSD-3-Clause', 'Apache-2.0',
  '(MPL-2.0 OR Apache-2.0)', 'OFL-1.1', 'Unlicense',
])

rmSync(output, { recursive: true, force: true })
mkdirSync(output, { recursive: true })
const manifest = [
  '# Bundled web dependencies',
  '',
  'Generated from web/package-lock.json. Each listed package is included in the production dependency graph.',
  'Its license or notice files are stored under npm/ using the package name and version.',
  '',
  '| Package | Version | Declared license |',
  '| --- | --- | --- |',
]
let count = 0
for (const [packagePath, metadata] of Object.entries(lock.packages)) {
  if (!packagePath || metadata.dev === true) continue
  const source = join(web, packagePath)
  if (!existsSync(source)) throw new Error(`Missing installed production package: ${packagePath}`)
  const packageMetadata = JSON.parse(readFileSync(join(source, 'package.json'), 'utf8'))
  const packageName = packageMetadata.name
  if (!packageName || typeof packageName !== 'string') throw new Error(`Missing package name: ${packagePath}`)
  const declaredLicense = metadata.license ?? packageMetadata.license ?? licenseOverrides.get(packageName)
  if (!allowedLicenses.has(declaredLicense)) throw new Error(`Unreviewed license for ${packageName}: ${declaredLicense ?? 'missing'}`)
  const licenseFiles = readdirSync(source, { withFileTypes: true })
    .filter(entry => entry.isFile() && /^(licen[cs]e|copying|notice)(\.|$)/i.test(entry.name))
    .map(entry => join(source, entry.name))
  if (licenseFiles.length === 0) {
    const replacement = fallback.get(packageName)
    if (!replacement || !existsSync(replacement)) throw new Error(`Missing license text for production package: ${packageName}`)
    licenseFiles.push(replacement)
  }
  const target = join(output, 'npm', ...packageName.split('/'), String(metadata.version))
  mkdirSync(target, { recursive: true })
  for (const license of licenseFiles) cpSync(license, join(target, license.substring(license.lastIndexOf('\\') + 1).substring(license.lastIndexOf('/') + 1)))
  manifest.push(`| ${packageName.replaceAll('|', '\\|')} | ${metadata.version ?? 'unknown'} | ${declaredLicense.replaceAll('|', '\\|')} |`)
  count++
}
writeFileSync(join(output, 'README.md'), manifest.join('\n') + '\n', 'utf8')
console.log(`Collected license files for ${count} production web packages in ${relative(root, output)}.`)

import { isAbsolute } from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = process.env.PLAYWRIGHT_MODULE_PATH;
export const { chromium, request } = await import(isAbsolute(modulePath) ? pathToFileURL(modulePath).href : modulePath);

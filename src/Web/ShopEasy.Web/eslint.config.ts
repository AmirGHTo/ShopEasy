import js from '@eslint/js'
import vitest from '@vitest/eslint-plugin'
import { defineConfig, globalIgnores } from 'eslint/config'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import testingLibrary from 'eslint-plugin-testing-library'
import playwright from 'eslint-plugin-playwright'
import globals from 'globals'
import tseslint from 'typescript-eslint'

export default defineConfig([
  globalIgnores(['dist', 'coverage', 'playwright-report', 'test-results']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      // the strict rule sets use the type information of tsconfig.*.json: they catch what the
      // compiler lets through (an unhandled promise, a condition that is always true, `any`)
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    rules: {
      // React escapes everything it renders; this is the one way to switch that off, so it is not used
      'no-restricted-syntax': [
        'error',
        {
          selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']",
          message: 'Do not insert HTML from a string: React escapes text for a reason (XSS).',
        },
      ],
      'no-eval': 'error',
      'no-new-func': 'error',
      eqeqeq: ['error', 'always'],
      // numbers in template strings are fine, the rest is a likely mistake
      '@typescript-eslint/restrict-template-expressions': ['error', { allowNumber: true }],
    },
  },
  {
    files: ['**/*.test.{ts,tsx}', 'src/test/**'],
    extends: [vitest.configs.recommended, testingLibrary.configs['flat/react']],
    languageOptions: { globals: globals.node },
    rules: {
      // the fetch mock is inspected on purpose
      '@typescript-eslint/unbound-method': 'off',
    },
  },
  {
    // the browser tests: they run in Node and drive a browser through Playwright's own API
    files: ['e2e/**/*.ts'],
    extends: [playwright.configs['flat/recommended']],
    languageOptions: { globals: globals.node },
  },
])

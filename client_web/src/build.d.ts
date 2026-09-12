// Modul: the two constants Vite's `define` substitutes at build time. Declared
// here because they exist only after substitution - there is no module to
// import them from, and without this every use is a type error.
//
// See scripts/stamp-version.mjs for what each one is for.

/** package.json's version. Semantic, bumped by hand, keys the "what's new" window. */
declare const __APP_VERSION__: string;

/** The moment this bundle was built. Changes every build; drives staleness detection. */
declare const __BUILD_ID__: string;

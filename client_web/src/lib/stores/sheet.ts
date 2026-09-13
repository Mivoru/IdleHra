// Modul: THE OPEN BOTTOM SHEET, for the hardware back button.
//
// On a phone PersonPicker opens as a sheet portalled to <body>, above
// everything else on the screen. Back means "close the thing I am looking at",
// so while a sheet is open the back button has to close IT - not walk to the
// previous screen underneath and take the half-made choice with it.
//
// A close function rather than a boolean, because the sheet's open state lives
// inside the component; App only needs a way to ask it to go away.
import { writable } from 'svelte/store';

export const openSheetCloser = writable<(() => void) | null>(null);

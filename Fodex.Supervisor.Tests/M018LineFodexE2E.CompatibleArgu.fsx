// TEST-ONLY DIAGNOSTIC BOOTSTRAP. This is not the production provider's startup sequence.
// Original target startup fails binding Argu 10.2.1 against already-loaded 10.1.400.
// Keep the production failure in the handoff; this probe attempts the downstream real-native chain.
#r @"F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\Argu.dll"
#load "M018LineFodexE2E.fsx"

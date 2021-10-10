# Changelog

### 1.3.0 (2021-10-10)
* Added `status` command
* Added `--toggle` option to `connect` command
* Fixed country name and code not matched case-insensitively in `connect`

### 1.2.1 (2021-06-15)
* Increase connection timeout for Windows (@lord-ne)

### 1.2.0 (2021-06-12)
* Add Windows support (@lord-ne)
* Switch from CoreRT to .Net 6 preview for single-file builds (much smaller but a bit slower)

### 1.1.0 (2020-12-19)
* Enable changing connected location with `--change` or `-c`
* Add `--random` option to connect to random location in country
* Other minor fixes

### 1.0.2 (2020-04-17)
* Fix timeout when connecting to the already selected location
* Make project work with mono and provide Hombrew formula (in addition to cask)

### 1.0.1 (2020-04-16)
* Fix browser extension showing the last selected instead of the currently connected location
* Show help if no action is specified

### 1.0.0 (2020-04-12)
* Initial release

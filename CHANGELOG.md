# Changelog

## 1.0.1

This release focuses on lower idle memory usage and several usability improvements.

- Significantly reduced idle memory usage by releasing inactive views and optimizing UI rendering.
- Reworked tray integration and main-window lifecycle to free resources while Drageon runs in the background.
- Improved component-list virtualization to reduce memory usage on the Packages page.
- Restored automatic loading of virtual hosts when opening the page.
- Extended log search to include timestamps and component names.
- Sorted the log component filter alphabetically and limited it to components with available log entries.

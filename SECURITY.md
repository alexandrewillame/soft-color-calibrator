# Security

Please report a security problem privately, not in a public issue: use **Report a vulnerability** in this
repository's Security tab. Fixes go into the next release; only the latest release is supported.

Good to know when looking for weaknesses:

- Soft Color Calibrator changes display settings (graphics card curves, monitor values over DDC/CI, HDR) and records
  each change so it can put it back. It asks for administrator rights only to turn Windows' own calibration loader on
  or back off, through a helper that refuses any other task.
- SoftCal Camera accepts a connection only from a PC that types the 6-digit code it shows, on the local network, and
  sends nothing else anywhere ([privacy policy](https://alexandrewillame.github.io/soft-color-calibrator/privacy.html)).

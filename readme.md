## ioSender - a gcode sender for grblHAL and Grbl controllers

---

Please check out the [Wiki](https://github.com/terjeio/Grbl-GCode-Sender/wiki) for further details.

8-bit Arduino controllers needs _Toggle DTR_ selected in order to reset the controller on connect. Behaviour may be erratic if not set.

![Toggle DTR](Media/Sender8.png)

---

#### This fork — proposed enhancements

This is the [`stevenrwood/ioSender`](https://github.com/stevenrwood/ioSender) fork. It carries a stack of proposed enhancements, each kept as a **clean, single-feature branch** (`pr/*`) that diffs against `master` so it can be reviewed — and picked up — independently.

**New here? Start with [`Overview.pdf`](Overview.pdf)** ([`overview.html`](overview.html)) — the big picture across all three coordinated forks (this **sender**, the grblHAL **Simulator**, and the iMXRT1062 **firmware**): goals & process, each fork's PRs, the `apply-prs` composer with examples, and links to every tracker.

**See [`Proposed-PRs.pdf`](Proposed-PRs.pdf) for this repo's full PR list**: every PR with its branch name, file-level diff stats, a description, and any stacking/dependency notes. (Same content as [`proposed-prs.html`](proposed-prs.html) if you'd rather open it in a browser.)

Branch model:
- `master` = the upstream release plus PRs 1&ndash;8 already integrated.
- Each remaining enhancement lives on its own `pr/<name>` branch off `master`. A few are **stacked** on another PR (e.g. the ATC macros branch builds on the SD-card filesystem branch) — `proposed-prs.html` lists each branch's *Depends on*.

##### Apply one (or more) to your own fork

Add this fork as a remote and fetch the branches:

```bash
git remote add srw https://github.com/stevenrwood/ioSender.git
git fetch srw
```

Then pick up a single PR. Look up its branch name (and any parent it stacks on) in `proposed-prs.html`, then either **merge the branch** as-is:

```bash
git merge srw/pr/<branch>
```

or **cherry-pick just its commits** onto your current branch (handy when your base differs from this fork's `master`):

```bash
# the commits a pr/* branch adds on top of master
git cherry-pick master..srw/pr/<branch>
```

For a **stacked** PR, apply its parent first (or cherry-pick from the parent instead of `master`):

```bash
git cherry-pick srw/pr/<parent-branch>..srw/pr/<branch>
```

Each branch touches a distinct, self-contained set of files, so independent PRs combine without conflicts; resolve any only where two PRs intentionally share a file (noted in the tracker).

#### Edge pre-releases

Edge pre-releases can be [downloaded from here](https://www.io-engineering.com/downloads), they contains changes yet to be incorporated in a main release and might be buggy and even break existing functionality.  
Use with care and please [post feedback](https://github.com/terjeio/ioSender/discussions/436) on any issues encountered!

No prereleases yet for v2.0.48.

#### General

If you want to test ioSender with grblHAL but do not have a board yet you can use the [grblHAL simulator](https://github.com/grblHAL/Simulator).
Build it with the [Web Builder](https://svn.io-engineering.com:8443/?driver=Simulator&board=Windows), unpack the .exe-files in the downloaded .zip somewhere and
open a command window (cmd or PowerShell) in the folder by \<Shift\>+Right clicking in it, select _Open PowerShell window here_ or
_Open command window here_ from the popup menu to open it.
Then find your computers IP address by typing `ipconfig` - the IP address can be found in the report generated.  
Run the simulator by typing `./grblHAL_sim -p 23` - 23 is the default Telnet port number and you may have to change it if a Telnet server is already running on the machine.
Leave the window open.  
Now start ioSender and select the _Network_ tab in the sender connection dialog, change the port number if you run the simulator with a different port,
type in your computers IP address and click _Ok_ to connect.  
You can run gcode programs, jog, access settings etc. but _not_ use gcodes that needs input - e.g. probing.  
The simulator can be stopped by typing \<Ctrl\>+C in the command window or by closing it.

If you ship ioSender with a prebuilt simulator for convenience, include the simulator executable (named `simulator.exe` or equivalent) in the installer .zip so it is placed next to the ioSender executable. The connection dialog has a new "Simulator" tab which can optionally start a local simulator and connect to `127.0.0.1:<port>` (default port 23).

---

Latest release is [2.0.47](https://github.com/terjeio/ioSender/releases/tag/2.0.47), see the [changelog](changelog.md) for details. 

---

Some UI examples:

![Sender](Media/Sender.png)

Main screen.
<br><br>

![3D view](Media/Sender2.png)

3D view of program, with live update of tool marker.
<br><br>

![3D view](Media/Sender2_XL.png)

XL version, German translation.
<br><br>

![Jog flyout](Media/Sender7.png)

Jogging flyout, supports up to 9 axes. The sender also supports keyboard jogging with \<Shift\> \(speed\) and \<Ctrl\> \(distance\) modifiers.
<br><br>

![Easy configuration](Media/Sender3.png)

Advanced grbl configuration with on-screen documentation. UI is dynamically generated from data in a file and/or from the controller.
<br><br>

![Probing options](Media/Sender4.png)

Probing options.
<br><br>

![Easy configuration](Media/Sender5.png)

Lathe mode.
<br><br>

![Easy configuration](Media/Sender6.png)

Conversational programming for Lathe Mode. Threading requires [grblHAL](https://github.com/grblHAL) controller with driver that has spindle sync support.

---
2026-04-29

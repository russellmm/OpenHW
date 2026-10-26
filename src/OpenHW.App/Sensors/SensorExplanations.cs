using System.Text.RegularExpressions;
using OpenHW.Hardware;

namespace OpenHW.App.Sensors;

/// <summary>
/// Plain-language explanations for sensor rows: what the reading is and when it matters, for tooltips and the tray
/// menu. Matched on the displayed name (and kind, where the same name means different things — "CPU" is both a
/// temperature and a fan header). Rows without a rule get a generic sentence from their kind.
/// </summary>
public static partial class SensorExplanations
{
    private sealed record Rule(Regex Name, HardwareKind? Kind, string Text);

    private static Rule R(string pattern, string text, HardwareKind? kind = null) =>
        new(new Regex("^(?:" + pattern + ")$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), kind, text);

    private static readonly Rule[] Rules =
    [
        // ── CPU temperatures ────────────────────────────────────────────────────────────────────────────────────
        R(@"CPU \(Tctl/Tdie\)", "The CPU's control temperature (Tctl), the value AMD's firmware and fan control act on. " +
            "On Threadripper it equals the hottest die (Tdie). It throttles near 95 °C; 60–85 °C under heavy load is normal."),
        R(@"CCD\d+ \(Tdie\)", "Temperature of one core complex die (8 cores and their L3 cache). Comparing CCDs shows which " +
            "chiplet is doing the work: an idle CCD sits several degrees cooler."),
        R(@"TSI0 \(CPU\)", "The CPU temperature as the motherboard's Super I/O chip reads it over AMD's SB-TSI interface. " +
            "It should match CPU (Tctl/Tdie); the board's fan curves use it."),
        R(@"CPU", "A motherboard estimate of CPU temperature (PECI/TSI calibration input). It tracks load about 11 °C " +
            "below Tctl; for the CPU itself, use CPU (Tctl/Tdie).", HardwareKind.Temperature),
        R(@"CPU \(Weighted Value\)", "A heavily averaged CPU temperature the board keeps for its fan control, so fans " +
            "don't chase every spike. It moves only a couple of degrees over minutes of full load."),
        R(@"CPUTIN", "A thermistor near the CPU socket on the motherboard. It reacts to CPU heat slowly (minutes), so it " +
            "shows the area around the socket rather than the CPU itself."),
        // ── CPU power, clocks and limits ───────────────────────────────────────────────────────────────────────
        R(@"CPU Package Power", "Power the whole CPU package is drawing right now, as the CPU's own firmware (SMU) reports " +
            "it. Compare with CPU PPT Limit: when they're equal the CPU is power-limited."),
        R(@"CPU PPT Limit", "Package Power Tracking: the most power the CPU is allowed to draw. Lowering it trades top " +
            "performance for heat and noise. Set it in Power → Power Limits."),
        R(@"Active Frequency Limit", "The highest clock the cores are allowed right now, after every limit (power, " +
            "current, temperature, rated maximum). The \"Frequency Limited by …\" rows say which one sets it."),
        R(@"Core Clock Limit", "The core clock ceiling from the CPU's boost limit (HSMP). It drops if a tool sets a boost " +
            "limit; otherwise it's the part's rated maximum."),
        R(@"Frequency Limited by OPN Max", "Yes when the cores are held only by the part's rated maximum clock — the " +
            "normal state when nothing else (power, heat, current) is limiting."),
        R(@"Frequency Limited by PPT Limit", "Yes when the CPU is at its package power limit (PPT): raising the limit " +
            "would let it clock higher; lowering it slows the CPU."),
        R(@"Frequency Limited by TDC limit", "Yes when the CPU is limited by the sustained current its voltage regulators " +
            "may deliver (Thermal Design Current)."),
        R(@"Frequency Limited by PROCHOT", "Yes when the motherboard has asserted PROCHOT (\"processor hot\") to slow the " +
            "CPU, usually because the VRMs are overheating."),
        R(@"Frequency Limited by cHTC-Active", "Yes when the CPU's own temperature control (HTC) is throttling it: the " +
            "CPU is too hot. Check cooling."),
        R(@"Frequency Limited by Reliability Limit", "Yes when firmware limits clocks to protect the silicon's long-term " +
            "reliability (voltage/temperature combinations it won't allow)."),
        R(@"Frequency Limited by APML Agent", "Yes when a platform management controller (a BMC, over AMD's APML) has set " +
            "a frequency limit."),
        R(@"Frequency Limited by HSMP Agent", "Yes when an operating-system tool has set a limit through HSMP (AMD's host " +
            "system management port), e.g. a boost limit."),
        R(@"Thermal Throttling \(PROCHOT\)", "Yes while the PROCHOT signal is asserted and the CPU is being slowed for heat. " +
            "Should always be No."),
        R(@"Infinity Fabric Clock \(FCLK\)", "Clock of AMD's Infinity Fabric, the interconnect between the core dies, " +
            "memory controller and I/O. Higher FCLK lowers memory and cross-die latency."),
        R(@"Memory Clock \(MCLK\)", "The DRAM clock: half the DDR transfer rate (2,800 MHz = DDR5-5600)."),
        R(@"Socket C0 Residency", "Share of time the CPU's cores spend awake and executing (C0), across the whole socket. " +
            "Low at idle; near 100 % under full load."),
        R(@"DRAM Bandwidth", "Memory traffic right now, reads plus writes, in GB/s. Compare with the peak in the tooltip's details."),
        R(@"DRAM Bandwidth Utilization", "Memory bandwidth in use as a share of the platform's peak."),
        R(@"Core Clocks|Core \d+ Clock", "Current clock of the core (both of its threads share one clock), as the kernel " +
            "reports it. Idle cores drop to their lowest clock to save power."),
        R(@"Max Core Clock", "The highest current clock among all cores — what the busiest core is running at."),
        R(@"Average Effective Clock|Core \d+ Effective Clock", "The clock the core actually delivered over the last moment, " +
            "counting time asleep (the kernel's APERF/MPERF average). An idle core reads low even if it boosts to full " +
            "speed for bursts — this is what work really got, unlike the plain clock."),
        R(@"CPU Package Power \(RAPL\)", "Package power from the CPU's RAPL energy counter (read through openhw-helper): " +
            "the energy used since the last poll divided by the time. Should track CPU Package Power from the SMU."),
        R(@"CPU Cores Power \(RAPL\)", "Power used by all CPU cores together (RAPL core domain), without the I/O die, " +
            "memory controller and Infinity Fabric — the rest of the package power is the SoC."),
        R(@"Core Usage|Core \d+ T\d+ Usage", "Share of time a thread was busy over the last poll (both SMT threads of a " +
            "core are listed)."),
        R(@"Total CPU Usage", "Share of all CPU threads' time spent busy over the last poll."),
        R(@"Max CPU/Thread Usage", "The busiest single thread's usage — 100 % here with low total usage means one " +
            "single-threaded job is limiting."),
        R(@"Core C\d+ Residency|Core \d+ C\d+ Residency", "Share of time cores spent in this idle state. Deeper states (higher " +
            "C numbers) save more power but take longer to wake."),
        // ── Super I/O voltages ─────────────────────────────────────────────────────────────────────────────────
        R(@"Vcore", "Voltage on the CPU core rail that feeds CCD0 and CCD3 (Vcore0). It rises with clock and load: ~0.8 V " +
            "idle, up to ~1.3 V at single-core boost."),
        R(@"CPU Core1", "Voltage on the second CPU core rail, feeding CCD1 and CCD2 (Vcore1). Confirmed by loading each " +
            "CCD in turn."),
        R(@"CPU VSOC", "Voltage of the CPU's SoC rail (I/O die, memory controller, Infinity Fabric). Fixed by BIOS settings."),
        R(@"CPU VDDIO", "Voltage of the CPU's memory I/O rail, set from the memory profile (EXPO/XMP) in BIOS."),
        R(@"VDD_11_S3 / MC", "Memory voltage rail (1.1 V class) feeding the DIMMs / memory controller."),
        R(@"\+12V", "The PSU's 12 V rail as the board measures it. Within ±5 % (11.4–12.6 V) is healthy."),
        R(@"\+5V", "The PSU's 5 V rail. Within ±5 % (4.75–5.25 V) is healthy."),
        R(@"3VCC", "The 3.3 V rail from the PSU. Within ±5 % (3.14–3.47 V) is healthy."),
        R(@"3VSB", "3.3 V standby rail: powered whenever the PSU is plugged in, even with the PC off."),
        R(@"AVSB", "Standby rail for the board's analog/monitoring circuits (3.3 V)."),
        R(@"VBAT", "The CMOS coin battery. Below about 2.8 V it should be replaced, or BIOS settings may be lost."),
        R(@"VTT", "Termination voltage for high-speed board signals (3.3 V on this board)."),
        R(@"VHIF", "A 3.3 V rail for the board's host interface logic."),
        R(@"CHIPSET\d+ 1\.0V|CHIPSET\d+ 1\.8V", "A chipset supply rail. It should hold steady at its nominal voltage."),
        R(@"VIN2|VIN9", "A Super I/O input with no published meaning on this board. Shown because HWiNFO shows it; values " +
            "are raw (×1)."),
        // ── Super I/O and EC temperatures ──────────────────────────────────────────────────────────────────────
        R(@"Motherboard", "Motherboard temperature from the Super I/O's system sensor — roughly the air inside the case near the board."),
        R(@"Temp2", "A board sensor (Super I/O AUXTIN0) whose location ASUS doesn't publish. It cools when fans speed up " +
            "under load, so it sits in the case airflow."),
        R(@"Temp9", "A board sensor (AUXTIN4) with no published location; steady at ambient-like values."),
        R(@"Temp14", "An unconnected Super I/O input (AUXTIN5); its ~97 °C is not a real temperature. Shown because HWiNFO shows it."),
        R(@"VRM_E|VRM_W", "Temperature of the CPU's voltage regulators, east or west bank. Under ~90 °C is comfortable; " +
            "they get hot under sustained all-core load.", HardwareKind.Temperature),
        R(@"T_Sensor", "The board's external thermistor header (T_SENSOR). Shows a real value only with a probe plugged in."),
        R(@"CPU Package", "Package temperature from the ASUS embedded controller.", HardwareKind.Temperature),
        // ── Fans ───────────────────────────────────────────────────────────────────────────────────────────────
        R(@"CPU", "Speed of the fan on the CPU_FAN header.", HardwareKind.Tach),
        R(@"Chassis \d+", "Speed of a case fan on a chassis fan header. 0 RPM means stopped or not connected."),
        R(@"Water Pump\+", "Speed of the pump on the W_PUMP+ header. A pump at 0 RPM means no coolant flow — the CPU " +
            "will overheat quickly."),
        R(@"CPU_Opt", "Speed of a fan on the CPU_OPT header (a second CPU-cooler fan). 0 RPM if none is connected."),
        R(@"VRM_E HS|VRM_W HS", "Speed of the small fan on the east or west VRM heatsink."),
        // ── Memory ─────────────────────────────────────────────────────────────────────────────────────────────
        R(@"SPD Hub Temperature.*", "Temperature of a DDR5 DIMM, from the SPD hub chip on the module. DDR5 starts slowing " +
            "refresh above ~85 °C; under ~60 °C is comfortable."),
        R(@"TS0 Temperature|TS1 Temperature", "Temperature at one end of the DIMM, from one of its two thermal sensors " +
            "(the SPD hub sits in the middle). DDR5 starts slowing refresh above ~85 °C; under ~60 °C is comfortable."),
        R(@"VDD \(SWA\) Voltage", "The DRAM chips' core supply (VDD), made on the DIMM by its PMIC from VIN. Set by the " +
            "memory profile (EXPO/XMP), e.g. 1.35 V; JEDEC default is 1.1 V."),
        R(@"VDDQ \(SWB\) Voltage", "The DRAM chips' I/O supply (VDDQ), made on the DIMM by its PMIC. Normally equal to VDD."),
        R(@"VPP \(SWC\) Voltage", "The DRAM wordline pump supply (VPP), made on the DIMM by its PMIC; about 1.8 V."),
        R(@"1\.8V VOUT Voltage|1\.0V VOUT Voltage", "Small supplies the DIMM's PMIC makes for the module's own chips (SPD " +
            "hub, thermal sensors, register clock driver)."),
        R(@"VIN Voltage", "The supply the board feeds the DIMM's PMIC (12 V on registered DIMMs, 5 V on desktop DIMMs).",
            HardwareKind.Voltage),
        R(@"Total Power", "Power the DIMM's PMIC delivers on its three main rails (VDD, VDDQ, VPP) together — the " +
            "module's power, in 0.125 W steps."),
        R(@"SW[ABC] Power|SW[ABC] Current", "Output of one of the DIMM PMIC's switching regulators (SWA = VDD, SWB = VDDQ, " +
            "SWC = VPP), in 0.125 W or 0.125 A steps."),
        R(@"PMIC High Temperature", "Yes when the DIMM's PMIC is hotter than its warning threshold. Should always be No."),
        R(@"PMIC Over Voltage", "Yes when the DIMM's PMIC sees too high an input or output voltage. Should always be No."),
        R(@"PMIC Under Voltage", "Yes when one of the DIMM PMIC's outputs dropped below its lockout level. Should always be No."),
        // ── Board voltage regulators ───────────────────────────────────────────────────────────────────────────
        R(@"VR Loop1|VR Loop2", "Temperature of the voltage regulator's power stages for each of its two control loops."),
        R(@"VR VOUT", "The regulator's output voltage. On the TRX50-SAGE this is the memory controller's VDD_11_S3 / " +
            "VDDIO_Mem rail, which follows the memory profile (1.35 V here)."),
        R(@"VR VIN", "The regulator's input: the board's 12 V supply."),
        R(@"Current \(IOUT\)|Current \(IIN\)", "Current out of (IOUT) and into (IIN) the voltage regulator."),
        R(@"Power \(POUT\)|Power \(Input\)", "Power the voltage regulator delivers (POUT) and draws from 12 V (Input)."),
        R(@"VRM Efficiency", "Output ÷ input power of the regulator. The two meters are coarse, so at light load this " +
            "reads high (capped at 100 %)."),
        R(@"(Rank \d+ )?Corrected Errors", "Memory errors ECC detected and fixed. A few over months is normal; a rising " +
            "count points to a failing DIMM or unstable memory settings."),
        R(@"(Rank \d+ )?Uncorrected Errors", "Memory errors ECC could not fix — data was lost. Should be 0; any count " +
            "needs attention."),
        R(@"Physical Memory Used|Physical Memory Available|Physical Memory Load", "RAM in use by programs and the kernel " +
            "(cache that can be freed counts as available)."),
        R(@"Swap Used|Swap Total|Swap Load", "Swap space on disk. Heavy swap use means RAM ran short and things slow down."),
        R(@"Virtual Memory Committed|Virtual Memory Commit Limit", "Memory programs have reserved, against how much the " +
            "kernel will promise (RAM plus swap)."),
        R(@"CPU Pressure.*|Memory Pressure.*|I/O Pressure.*", "Linux pressure stall information: share of time tasks " +
            "waited for this resource. \"some\" = at least one task waiting, \"full\" = all were. Sustained values mean a bottleneck."),
        R(@"Load Average.*", "Average number of tasks running or waiting to run. Compare with the thread count (64 here)."),
        // ── GPUs ───────────────────────────────────────────────────────────────────────────────────────────────
        R(@"GPU Temperature", "GPU core temperature. NVIDIA cards slow down near 83–90 °C."),
        R(@"GPU Power", "Power the graphics card is drawing right now, board total."),
        R(@"GPU Power Limit", "The card's current power limit. Set it in Power → Power Limits."),
        R(@"GPU Core Load", "Share of time the GPU's cores were busy."),
        R(@"GPU Memory Controller Load", "Share of time the GPU's memory interface was busy — high for memory-bound work."),
        R(@"GPU Clock|GPU SM Clock", "GPU core clock (the SM clock drives the shader cores)."),
        R(@"GPU Memory Clock", "GPU video memory clock."),
        R(@"GPU Video Clock", "Clock of the GPU's video encode/decode engines."),
        R(@"GPU Memory Allocated|GPU Memory Total", "Video memory in use, and the card's total."),
        R(@"GPU Fan\d+", "GPU fan speed as a share of its maximum. 0 % at idle is normal: many cards stop their fans when cool."),
        R(@"GPU Performance State", "NVIDIA performance state: P0 is full speed, higher numbers are lower-power states."),
        R(@"GPU Performance Limit - Power", "Yes when the card is held back by its power limit."),
        R(@"GPU Performance Limit - Thermal", "Yes when the card is held back by temperature."),
        R(@"GPU Performance Limit - Reliability", "Yes when the card is held back by its voltage reliability limit."),
        R(@"GPU Performance Limit - Utilization", "Yes when the card is at lower clocks simply because there isn't enough work."),
        R(@"GPU Video Encoder Load|GPU Video Decoder Load", "Use of the GPU's dedicated video encoder/decoder (NVENC/NVDEC)."),
        R(@"GPU PCIe Rx Rate|GPU PCIe Tx Rate", "Data moving over PCIe to (Rx) or from (Tx) the card."),
        R(@"GPU Power \(1 s Average\)", "The card's power averaged over the last second (NVML), steadier than the instant GPU Power."),
        R(@"GPU PCIe Correctable Errors|GPU PCIe NAKs Received|GPU PCIe NAKs Sent|GPU PCIe Receiver Errors|GPU PCIe Bad TLPs|GPU PCIe Bad DLLPs|GPU PCIe LCRC Errors",
            "PCIe link errors the hardware detected and recovered from (by retrying). A few are harmless; a count that keeps " +
            "rising points to a marginal link — riser cable, slot or PCIe generation set too high in BIOS."),
        R(@"GPU PCIe Non-Fatal Errors|GPU PCIe Fatal Errors|GPU PCIe Unsupported Requests", "Uncorrectable PCIe errors on the " +
            "card's link. Should be 0; any count deserves a look (cabling, slot, BIOS PCIe settings, driver)."),
        R(@"PCIe Link Speed|PCIe Link Width", "The negotiated PCIe link. Idle cards drop to a lower speed to save power."),
        // ── Drives ─────────────────────────────────────────────────────────────────────────────────────────────
        R(@"Drive Temperature", "The SSD's temperature. NVMe drives throttle around 70–80 °C."),
        R(@"Available Spare", "Spare flash blocks left for replacing worn ones, from 100 %. Below the Spare Warning " +
            "Threshold the drive reports a warning."),
        R(@"Spare Warning Threshold", "The Available Spare level at which the drive raises a warning."),
        R(@"Endurance Used", "Share of the drive's rated write endurance used (can exceed 100 %; the drive keeps working " +
            "but is past its rating)."),
        R(@"Critical Warning Flags", "The drive's own critical warning bits. 0 means no warning."),
        R(@"Media / Data Integrity Errors", "Unrecoverable data errors the drive detected. Should be 0."),
        R(@"Unsafe Shutdowns", "Times the drive lost power without a clean shutdown."),
        R(@"Power Cycles|Power-On Time", "How often, and how long, the drive has been powered."),
        R(@"Lifetime Data Read|Lifetime Data Written|Lifetime Read Commands|Lifetime Write Commands", "Totals over the drive's life."),
        R(@"Error-Log Entries", "Entries in the drive's error log over its life; often harmless (e.g. unsupported commands)."),
        R(@"Time Above Warning Temperature|Time Above Critical Temperature", "Minutes the drive has spent above its " +
            "warning/critical temperature over its life."),
        R(@"Thermal Management Stage.*", "The drive's own heat throttling: how often and how long it slowed itself to cool down."),
        R(@"Read Rate|Write Rate|Read Activity|Write Activity|Total Activity|Controller Busy Time", "How busy the drive is right now."),
        R(@"Read Total Since Device Start|Write Total Since Device Start", "Data read/written since the drive was last powered on."),
        // ── Network ────────────────────────────────────────────────────────────────────────────────────────────
        R(@"Current DL Rate|Current UP Rate|Total DL|Total UP", "Network traffic on this interface: rate now, and totals since it came up."),
        R(@"MAC Temperature|PHY Temperature", "Temperature of the network chip (the MAC or the PHY that drives the cable)."),
        R(@"Internal temperature.*", "A temperature inside the network adapter's chip."),
    ];

    /// <summary>What the row measures, in plain words; null when nothing better than the name is known.</summary>
    public static string Explain(HardwareItem item, string displayName)
    {
        foreach (var rule in Rules)
            if ((rule.Kind is null || rule.Kind == item.Kind) && rule.Name.IsMatch(displayName)) return rule.Text;
        return item.Kind switch
        {
            HardwareKind.Temperature => "A temperature sensor.",
            HardwareKind.Voltage => "A voltage measured by the board.",
            HardwareKind.Tach => "A fan or pump speed.",
            HardwareKind.Power => "A power reading.",
            HardwareKind.Frequency => "A clock frequency.",
            HardwareKind.Load => "A utilisation reading.",
            _ => "",
        };
    }
}

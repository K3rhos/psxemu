namespace PSXEmu;

/// <summary>
/// SBI (Subchannel Binary Information) file parser + LibCrypt-protected
/// game registry.
///
/// LibCrypt is a Sony anti-piracy scheme used on many PAL PSX titles.
/// The protection works via DELIBERATELY-WRONG subchannel Q data on
/// specific "protected" sectors of the original retail disc. When the
/// game issues `GetLocP` (CDROM command 0x11) for a protected sector,
/// the real disc returns intentionally-corrupted MSF values that don't
/// match the laser's actual position. The game's anti-piracy code
/// compares these values against a hard-coded expected pattern; if they
/// match (= original disc), the game runs normally. On a copied disc
/// the subchannel Q is correctly mastered, so the values don't match
/// and the game either refuses to play (e.g. Dino Crisis sits on the
/// gore-warning screen forever) or silently corrupts FMV / save data.
///
/// SBI files capture the correct "wrong" subchannel Q data for the
/// protected sectors. By substituting these bytes when the game reads
/// GetLocP, we get the same behaviour the retail disc would produce
/// without needing to reproduce the physical disc pressing trick.
///
/// File format (standard SBI, 4-byte header + N entries of 14 bytes):
///   Offset 0..3   : magic "SBI\0" (0x53 0x42 0x49 0x00)
///   Then N entries of:
///     Offset 0..2 : Sector MSF (BCD: MM, SS, FF)
///     Offset 3    : Type byte (always 0x01, subchannel Q replacement)
///     Offset 4..13: 10 bytes of replacement subchannel Q data:
///         [0]   Control/ADR  (e.g. 0x41 = data track, ADR=1)
///         [1]   Track number (BCD)
///         [2]   Index number (BCD; usually 0x01)
///         [3..5] Relative MM/SS/FF (BCD)
///         [6]   Reserved (always 0)
///         [7..9] Absolute MM/SS/FF (BCD)
/// </summary>
public static class PsxSbi
{
	// Magic header that identifies a standard SBI file. The 4th byte is
	// 0x00 (NUL), (`expected_header[] = {'S', 'B', 'I', '\0'}`). A previous version of
	// this file used 0x1A which is the magic for *some* other formats (e.g.
	// MS-DOS EOF) and is occasionally documented as "SBI" in stale references,
	// but the actual on-disk format is NUL-terminated.
	private static readonly byte[] Magic = { (byte)'S', (byte)'B', (byte)'I', 0x00 };

	/// <summary>
	/// Parses an SBI file's bytes into a dictionary mapping each protected
	/// sector's LBA to its 10-byte replacement subchannel Q data. Returns
	/// null and logs an error if the file is malformed.
	/// </summary>
	public static Dictionary<uint, byte[]> Parse(byte[] data)
	{
		if (data == null || data.Length < 4)
		{
			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn, "[SBI] File too small or null");
			return null;
		}

		// Validate magic.
		for (int i = 0; i < 4; i++)
		{
			if (data[i] != Magic[i])
			{
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
					$"[SBI] Invalid magic header, expected 'SBI\\0' (0x53 0x42 0x49 0x00), got " +
					$"0x{data[0]:X2} 0x{data[1]:X2} 0x{data[2]:X2} 0x{data[3]:X2}");
				return null;
			}
		}

		// Each entry is 14 bytes: 3 MSF + 1 type + 10 data.
		const int EntrySize = 14;
		int payloadBytes = data.Length - 4;
		if (payloadBytes % EntrySize != 0)
		{
			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
				$"[SBI] Payload size {payloadBytes} is not a multiple of {EntrySize}, file may be truncated");
		}

		int entryCount = payloadBytes / EntrySize;
		var map = new Dictionary<uint, byte[]>(entryCount);
		uint minLba = uint.MaxValue;
		uint maxLba = 0;

		for (int i = 0; i < entryCount; i++)
		{
			int offset = 4 + i * EntrySize;
			byte mmBcd = data[offset + 0];
			byte ssBcd = data[offset + 1];
			byte ffBcd = data[offset + 2];
			byte type  = data[offset + 3];

			if (!IsValidPackedBcd(mmBcd) || !IsValidPackedBcd(ssBcd) || !IsValidPackedBcd(ffBcd) ||
				ssBcd >= 0x60 || ffBcd >= 0x75)
			{
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
					$"[SBI] Entry #{i} has invalid MSF {mmBcd:X2}:{ssBcd:X2}:{ffBcd:X2}");
				return null;
			}

			if (type != 0x01)
			{
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
					$"[SBI] Entry #{i} has invalid type 0x{type:X2} (expected 0x01)");
				return null;
			}

			// BCD -> integer, then MSF -> LBA (the standard PSX +150 offset is
			// applied so the LBA matches the value `LbaToMsf` would generate
			// in PsxCdrom.cs for the same sector).
			int mm = BcdToInt(mmBcd);
			int ss = BcdToInt(ssBcd);
			int ff = BcdToInt(ffBcd);
			int absoluteFrames = ((mm * 60) + ss) * 75 + ff;
			if (absoluteFrames < 150)
			{
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
					$"[SBI] Entry #{i} points before 00:02:00 ({mmBcd:X2}:{ssBcd:X2}:{ffBcd:X2})");
				return null;
			}
			uint lba = (uint)(absoluteFrames - 150);

			var subQ = new byte[10];
			Array.Copy(data, offset + 4, subQ, 0, 10);
			map[lba] = subQ;
			if (lba < minLba) minLba = lba;
			if (lba > maxLba) maxLba = lba;
		}

		PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
			$"[SBI] Loaded {map.Count} protected-sector replacements (internal LBA {minLba}..{maxLba})");
		return map;
	}

	private static int BcdToInt(byte b) => ((b >> 4) & 0xF) * 10 + (b & 0xF);
	private static bool IsValidPackedBcd(byte b) => ((b >> 4) <= 9) && ((b & 0x0F) <= 9);

	private static readonly HashSet<string> LibCryptGames = new(StringComparer.OrdinalIgnoreCase)
	{
	    "SLES_012.26", // Actua Ice Hockey 2 (PAL)
	    "SLES_025.63", // Anstoss - Premier Manager (PAL Germany)
	    "SCES_015.64", // Ape Escape (PAL English)
	    "SCES_020.28", // Ape Escape (PAL French)
	    "SCES_020.29", // Ape Escape (PAL German)
	    "SCES_020.30", // Ape Escape (PAL Italian)
	    "SCES_020.31", // Ape Escape (PAL Spanish)
	    "SLES_033.24", // Asterix - Mega Madness (PAL)
	    "SCES_023.66", // Barbie - Aventure Equestre (PAL French)
	    "SCES_023.65", // Barbie - Race & Ride (PAL English)
	    "SCES_023.67", // Barbie - Race & Ride (PAL German)
	    "SCES_023.68", // Barbie - Race & Ride (PAL Italian)
	    "SCES_023.69", // Barbie - Race & Ride (PAL Spanish)
	    "SCES_024.88", // Barbie - Sports Extreme (PAL French)
	    "SCES_024.87", // Barbie - Super Sports (PAL English)
	    "SCES_024.89", // Barbie - Super Sports (PAL German)
	    "SCES_024.90", // Barbie - Super Sports (PAL Italian)
	    "SCES_024.91", // Barbie - Super Sports (PAL Spanish)
	    "SLES_029.77", // BDFL Manager 2001 (PAL German)
	    "SLES_036.05", // BDFL Manager 2002 (PAL German)
	    "SLES_022.93", // Canal+ Premier Manager (PAL M3)
	    "SCES_028.34", // Crash Bash (PAL M5)
	    "SCES_021.05", // CTR - Crash Team Racing (EDC Version) (PAL M5)
	    "SCES_021.05", // CTR - Crash Team Racing (No EDC Version) (PAL M5)
	    "SLES_022.07", // Dino Crisis (PAL English)
	    "SLES_022.08", // Dino Crisis (PAL French)
	    "SLES_022.09", // Dino Crisis (PAL German)
	    "SLES_022.10", // Dino Crisis (PAL Italian)
	    "SLES_022.11", // Dino Crisis (PAL Spanish)
	    "SLES_031.89", // Disney's 102 Dalmatians - Puppies to the Rescue (PAL English)
	    "SLES_031.91", // Disney's 102 Dalmatians - Puppies to the Rescue (PAL M5)
	    "SCES_020.06", // Disney's Libro Animato Creativo - Mulan (PAL Italian)
	    "SCES_020.05", // Disney's Mulan (PAL German)
	    "SCES_020.07", // Disney's Mulan (PAL Spanish)
	    "SCES_016.95", // Disney's Story Studio - Mulan (PAL English)
	    "SCES_014.31", // Disney's Tarzan (PAL English)
	    "SCES_021.85", // Disney's Tarzan (PAL Dutch)
	    "SCES_015.16", // Disney's Tarzan (PAL French)
	    "SCES_015.17", // Disney's Tarzan (PAL German)
	    "SCES_015.18", // Disney's Tarzan (PAL Italian)
	    "SCES_015.19", // Disney's Tarzan (PAL Spanish)
	    "SCES_021.82", // Disney's Tarzan (PAL Swedish)
	    "SCES_022.64", // Disney's Verhalenstudio - Mulan (PAL Dutch)
	    "SLES_025.38", // EA Sports Superbike 2000 (PAL M6)
	    "SLES_017.15", // Eagle One Harrier Attack (PAL M5)
	    "SCES_017.04", // Esto Es Futbol (PAL Spanish)
	    "SLES_027.22", // F1 2000 (PAL M4)
	    "SLES_027.24", // F1 2000 (PAL Italian)
	    "SLES_029.65", // Final Fantasy IX Disc 1 (PAL English)
	    "SLES_129.65", // Final Fantasy IX Disc 2 (PAL English)
	    "SLES_229.65", // Final Fantasy IX Disc 3 (PAL English)
	    "SLES_329.65", // Final Fantasy IX Disc 4 (PAL English)
	    "SLES_029.66", // Final Fantasy IX Disc 1 (PAL French)
	    "SLES_129.66", // Final Fantasy IX Disc 2 (PAL French)
	    "SLES_229.66", // Final Fantasy IX Disc 3 (PAL French)
	    "SLES_329.66", // Final Fantasy IX Disc 4 (PAL French)
	    "SLES_029.67", // Final Fantasy IX Disc 1 (PAL German)
	    "SLES_129.67", // Final Fantasy IX Disc 2 (PAL German)
	    "SLES_229.67", // Final Fantasy IX Disc 3 (PAL German)
	    "SLES_329.67", // Final Fantasy IX Disc 4 (PAL German)
	    "SLES_029.68", // Final Fantasy IX Disc 1 (PAL Italian)
	    "SLES_129.68", // Final Fantasy IX Disc 2 (PAL Italian)
	    "SLES_229.68", // Final Fantasy IX Disc 3 (PAL Italian)
	    "SLES_329.68", // Final Fantasy IX Disc 4 (PAL Italian)
	    "SLES_029.69", // Final Fantasy IX Disc 1 (PAL Spanish)
	    "SLES_129.69", // Final Fantasy IX Disc 2 (PAL Spanish)
	    "SLES_229.69", // Final Fantasy IX Disc 3 (PAL Spanish)
	    "SLES_329.69", // Final Fantasy IX Disc 4 (PAL Spanish)
	    "SLES_020.80", // Final Fantasy VIII Disc 1 (PAL English)
	    "SLES_120.80", // Final Fantasy VIII Disc 2 (PAL English)
	    "SLES_220.80", // Final Fantasy VIII Disc 3 (PAL English)
	    "SLES_320.80", // Final Fantasy VIII Disc 4 (PAL English)
	    "SLES_020.81", // Final Fantasy VIII Disc 1 (PAL French)
	    "SLES_120.81", // Final Fantasy VIII Disc 2 (PAL French)
	    "SLES_220.81", // Final Fantasy VIII Disc 3 (PAL French)
	    "SLES_320.81", // Final Fantasy VIII Disc 4 (PAL French)
	    "SLES_020.82", // Final Fantasy VIII Disc 1 (PAL German)
	    "SLES_120.82", // Final Fantasy VIII Disc 2 (PAL German)
	    "SLES_220.82", // Final Fantasy VIII Disc 3 (PAL German)
	    "SLES_320.82", // Final Fantasy VIII Disc 4 (PAL German)
	    "SLES_020.83", // Final Fantasy VIII Disc 1 (PAL Italian)
	    "SLES_120.83", // Final Fantasy VIII Disc 2 (PAL Italian)
	    "SLES_220.83", // Final Fantasy VIII Disc 3 (PAL Italian)
	    "SLES_320.83", // Final Fantasy VIII Disc 4 (PAL Italian)
	    "SLES_020.84", // Final Fantasy VIII Disc 1 (PAL Spanish)
	    "SLES_120.84", // Final Fantasy VIII Disc 2 (PAL Spanish)
	    "SLES_220.84", // Final Fantasy VIII Disc 3 (PAL Spanish)
	    "SLES_320.84", // Final Fantasy VIII Disc 4 (PAL Spanish)
	    "SLES_029.78", // Football Manager Campionato 2001 (PAL Italian)
	    "SCES_019.79", // Formula One 99 (PAL M4)
	    "SLES_027.67", // Frontschweine \[Hogs of War\] (PAL German)
	    "SCES_017.02", // Fussball Live (PAL German)
	    "SLES_030.62", // Fussball Manager 2001 (PAL German)
	    "SLES_023.28", // Galerians Disc 1 (PAL English)
	    "SLES_123.28", // Galerians Disc 2 (PAL English)
	    "SLES_223.28", // Galerians Disc 3 (PAL English)
	    "SLES_023.29", // Galerians Disc 1 (PAL French)
	    "SLES_123.29", // Galerians Disc 2 (PAL French)
	    "SLES_223.29", // Galerians Disc 3 (PAL French)
	    "SLES_023.30", // Galerians Disc 1 (PAL German)
	    "SLES_123.30", // Galerians Disc 2 (PAL German)
	    "SLES_223.30", // Galerians Disc 3 (PAL German)
	    "SLES_012.41", // Gekido - Urban Fighters (PAL M5)
	    "SLES_010.41", // Hogs of War (PAL English)
	    "SCES_014.44", // Jackie Chan Stuntmaster (PAL English)
	    "SLES_013.62", // Le Mans 24 Hours (PAL M6)
	    "SCES_017.01", // Le Monde des Bleus - Le Jeu Officiel de l'Equipe de France (PAL French)
	    "SLES_013.01", // Legacy of Kain - Soul Reaver (PAL English)
	    "SLES_020.24", // Legacy of Kain - Soul Reaver (PAL French)
	    "SLES_020.25", // Legacy of Kain - Soul Reaver (PAL German)
	    "SLES_020.27", // Legacy of Kain - Soul Reaver (PAL Italian)
	    "SLES_020.26", // Legacy of Kain - Soul Reaver (PAL Spanish)
	    "SLES_027.66", // Les Cochons de Guerre \[Hogs of War\] (PAL French)
	    "SLES_029.75", // LMA Manager 2001 (PAL English)
	    "SLES_036.03", // LMA Manager 2002 (PAL English)
	    "SLES_035.30", // Lucky Luke - Western Fever (PAL M6)
	    "SCES_003.11", // MediEvil (PAL English)
	    "SCES_014.92", // MediEvil (PAL French)
	    "SCES_014.93", // MediEvil (PAL German)
	    "SCES_014.94", // MediEvil (PAL Italian)
	    "SCES_014.95", // MediEvil (PAL Spanish)
	    "SCES_025.44", // MediEvil 2 (PAL M3 English/French/German)
	    "SCES_025.45", // MediEvil 2 (PAL M3 Spanish/Italian/Portuguese)
	    "SCES_025.46", // MediEvil 2 (PAL Russian)
	    "SLES_035.19", // Men in Black - The Series - Crashdown (PAL English)
	    "SLES_035.20", // Men in Black - The Series - Crashdown (PAL French)
	    "SLES_035.21", // Men in Black - The Series - Crashdown (PAL German)
	    "SLES_035.22", // Men in Black - The Series - Crashdown (PAL Italian)
	    "SLES_035.23", // Men in Black - The Series - Crashdown (PAL Spanish)
	    "SLES_015.45", // Michelin Rally Masters - Race of Champions (PAL M3 English/German/Swedish)
	    "SLES_023.95", // Michelin Rally Masters - Race of Champions (PAL M3 French/Italian/Spanish)
	    "SLES_028.39", // Mike Tyson Boxing (PAL M5)
	    "SLES_019.06", // Mission Impossible (PAL M5)
	    "SLES_028.30", // MoHo (PAL M5)
	    "SLES_026.89", // Need for Speed - Porsche 2000 (PAL M3 English/German/Swedish)
	    "SLES_027.00", // Need for Speed - Porsche 2000 (PAL M3 French/Spanish/Italian)
	    "SLES_020.86", // N-Gen Racing (PAL M5)
	    "SLES_025.58", // Parasite Eve II Disc 1 (PAL English)
	    "SLES_125.58", // Parasite Eve II Disc 2 (PAL English)
	    "SLES_025.59", // Parasite Eve II Disc 1 (PAL French)
	    "SLES_125.59", // Parasite Eve II Disc 2 (PAL French)
	    "SLES_025.60", // Parasite Eve II Disc 1 (PAL German)
	    "SLES_125.60", // Parasite Eve II Disc 2 (PAL German)
	    "SLES_025.61", // Parasite Eve II Disc 1 (PAL Spanish)
	    "SLES_125.61", // Parasite Eve II Disc 2 (PAL Spanish)
	    "SLES_025.62", // Parasite Eve II Disc 1 (PAL Italian)
	    "SLES_125.62", // Parasite Eve II Disc 2 (PAL Italian)
	    "SLES_029.92", // Premier Manager 2000 (PAL English)
	    "SLES_000.17", // Prince Naseem Boxing (PAL M5)
	    "SLES_019.43", // Radikal Bikers (PAL M5)
	    "SLES_028.24", // RC Revenge (PAL M4)
	    "SLES_025.29", // Resident Evil 3 - Nemesis (PAL English)
	    "SLES_025.30", // Resident Evil 3 - Nemesis (PAL French)
	    "SLES_025.31", // Resident Evil 3 - Nemesis (PAL German)
	    "SLES_026.98", // Resident Evil 3 - Nemesis (PAL Ireland)
	    "SLES_025.33", // Resident Evil 3 - Nemesis (PAL Italian)
	    "SLES_025.32", // Resident Evil 3 - Nemesis (PAL Spanish)
	    "SLES_009.95", // Ronaldo V-Football (PAL M4 English/French/Dutch/Swedish)
	    "SLES_026.81", // Ronaldo V-Football (PAL M4 German/Spanish/Italian/Portuguese)
	    "SLES_021.12", // Saga Frontier 2 (PAL English)
	    "SLES_021.13", // Saga Frontier 2 (PAL French)
	    "SLES_021.18", // Saga Frontier 2 (PAL German)
	    "SLES_027.63", // Sno-Cross Championship Racing (PAL M5)
	    "SCES_022.90", // Space Debris (PAL English)
	    "SCES_024.30", // Space Debris (PAL French)
	    "SCES_024.31", // Space Debris (PAL German)
	    "SCES_024.32", // Space Debris (PAL Italian)
	    "SCES_017.63", // Speed Freaks (PAL English)
	    "SCES_021.04", // Spyro 2 - Gateway to Glimmer (PAL M5)
	    "SCES_028.35", // Spyro - Year of the Dragon v1.0 (PAL M5)
	    "SCES_028.35", // Spyro - Year of the Dragon v1.1 Platinum (PAL M5)
	    "SLES_028.57", // Sydney 2000 (PAL English)
	    "SLES_028.58", // Sydney 2000 (PAL French)
	    "SLES_028.59", // Sydney 2000 (PAL German)
	    "SLES_028.61", // Sydney 2000 (PAL Spanish)
	    "SLES_032.45", // Technomage - De Terugkeer der Eeuwigheid (PAL Dutch)
	    "SLES_028.31", // Technomage - Die Rueckkehr der Ewigkeit (PAL German)
	    "SLES_032.42", // Technomage - En Quete de l'Eternite (PAL French)
	    "SLES_032.41", // Technomage - Return of Eternity (PAL English)
	    "SLES_034.89", // The Italian Job (PAL English)
	    "SLES_036.26", // The Italian Job (PAL German)
	    "SLES_026.88", // Theme Park World (PAL M7)
	    "SCES_017.00", // This Is Football (PAL English)
	    "SCES_017.03", // This Is Football (PAL Italian)
	    "SCES_018.82", // This Is Football (PAL French/Dutch)
	    "SLES_025.72", // TOCA - World Touring Cars (PAL M3 English/French/German)
	    "SLES_025.73", // TOCA - World Touring Cars (PAL Italian/Spanish)
	    "SLES_027.04", // UEFA Euro 2000 (PAL English)
	    "SLES_027.05", // UEFA Euro 2000 (PAL French)
	    "SLES_027.06", // UEFA Euro 2000 (PAL German)
	    "SLES_027.07", // UEFA Euro 2000 (PAL Italian)
	    "SLES_017.33", // UEFA Striker (PAL M6)
	    "SLES_020.71", // Urban Chaos (PAL English/Italian/Spanish)
	    "SLES_023.55", // Urban Chaos (PAL German)
	    "SLES_027.54", // Vagrant Story (PAL English)
	    "SLES_027.55", // Vagrant Story (PAL French)
	    "SLES_027.56", // Vagrant Story (PAL German)
	    "SLES_019.07", // V-Rally - Championship Edition 2 (PAL M3)
	    "SLES_027.33", // Walt Disney's World Quest - Magical Racing Tour (PAL M9)
	};

	/// <summary>
	/// Returns true if the given game serial is known to require SBI data to run correctly.
	/// </summary>
	public static bool RequiresSbi(string serial)
	{
		if (string.IsNullOrWhiteSpace(serial)) return false;
		return LibCryptGames.Contains(serial.Trim());
	}
}

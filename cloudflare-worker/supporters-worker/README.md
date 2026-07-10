# POE2GPS Supporters Worker

Cloudflare Worker that:
- Receives Ko-fi webhooks on every donation.
- Mints an Ed25519-signed supporter code and emails it to the donor.
- Assigns the `☕ Supporter` role in your Discord server (when the donor pastes their Discord handle in the Ko-fi message).
- Posts a `🎉 New supporter!` announcement to a Discord channel (optional).

The POE2GPS app verifies the signed code offline against a shipped Ed25519 public key — no per-donor release, no shipped hash list.

## Deploy checklist

1. **Generate an Ed25519 keypair.**

    ```bash
    python -c "
    from cryptography.hazmat.primitives.asymmetric import ed25519
    from cryptography.hazmat.primitives import serialization
    priv = ed25519.Ed25519PrivateKey.generate()
    pub  = priv.public_key()
    print('SIGNING_PRIVATE_KEY_HEX:', priv.private_bytes(encoding=serialization.Encoding.Raw, format=serialization.PrivateFormat.Raw, encryption_algorithm=serialization.NoEncryption()).hex())
    print('PUBLIC KEY (ship in POE2GPS supporter_public_key.txt):', pub.public_bytes(encoding=serialization.Encoding.Raw, format=serialization.PublicFormat.Raw).hex())
    "
    ```

2. **Ship the public key in POE2GPS.** Write the public hex to `src/POE2Radar.Core/Support/supporter_public_key.txt` (one line, no trailing whitespace). Commit + release POE2GPS — this file is embedded in the exe.

3. **Set the Worker secrets.**

    ```bash
    cd cloudflare-worker/supporters-worker
    wrangler secret put SIGNING_PRIVATE_KEY_HEX
    wrangler secret put KO_FI_VERIFICATION_TOKEN     # from Ko-fi webhook settings
    wrangler secret put RESEND_API_KEY               # optional but recommended
    wrangler secret put RESEND_FROM                  # e.g. supporters@yourdomain.com
    wrangler secret put DISCORD_BOT_TOKEN            # from your Discord app
    wrangler secret put DISCORD_GUILD_ID             # your Discord server ID
    wrangler secret put DISCORD_SUPPORTER_ROLE_ID    # the ☕ Supporter role ID
    wrangler secret put DISCORD_ANNOUNCE_WEBHOOK     # optional
    ```

4. **Deploy the Worker.**

    ```bash
    wrangler deploy
    ```

    Note the deployed URL (e.g. `https://poe2gps-supporters.YOUR-ACCOUNT.workers.dev`).

5. **Wire Ko-fi.** In Ko-fi's webhook settings page, set the webhook URL to
    `https://poe2gps-supporters.YOUR-ACCOUNT.workers.dev/ko-fi-webhook`. Copy the verification token
    Ko-fi shows you into the `KO_FI_VERIFICATION_TOKEN` secret above (redeploy after).

6. **Discord bot setup:**
    - Create a Discord app + bot at https://discord.com/developers/applications.
    - Enable the `bot` scope with the `Manage Roles` permission.
    - Invite the bot to your server: `https://discord.com/oauth2/authorize?client_id=<APP_ID>&scope=bot&permissions=268435456` (268435456 = Manage Roles).
    - Ensure the bot's role position in **Server Settings → Roles** is ABOVE the `☕ Supporter` role.
    - Grab the bot token from the app's Bot page, the guild ID by right-clicking your server name (need Developer Mode on), and the role ID by right-clicking the `☕ Supporter` role in Server Settings.

7. **Ko-fi donor UX:** on your Ko-fi page, add a note that donors can include their Discord handle in the donation message for an auto-role. Example: `discord: myhandle` in the message field.

## Testing

- **Local sanity:** `wrangler dev` starts a local server; POST a Ko-fi-shaped body to `http://localhost:8787/ko-fi-webhook`:

    ```bash
    curl -X POST http://localhost:8787/ko-fi-webhook \
      -H "Content-Type: application/x-www-form-urlencoded" \
      --data-urlencode 'data={"verification_token":"YOUR_TOKEN","email":"test@example.com","amount":"5.00","message":"discord: yourdiscord","from_name":"Testor"}'
    ```

    You should get a 200 with a JSON body containing a fresh `poe2gps.<payload>.<sig>` code.

- **Public key sanity:** GET `/public-key` on the deployed Worker and confirm the hex matches `src/POE2Radar.Core/Support/supporter_public_key.txt` in POE2GPS. If they diverge, you rotated one without the other — the app won't validate new codes.

## Rotation

- **Rotate the private key** if it leaks: generate a new keypair, update `SIGNING_PRIVATE_KEY_HEX`, ship a POE2GPS release with the new public key. Old codes stop validating.
- **Revoke a specific supporter** (rare): add a small `REVOKED_EMAILS` list to the client validator or maintain server-side. Not shipped by default — build if you actually need it.

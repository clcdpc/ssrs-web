# This is the only unpublished-toolkit integration boundary. No API name,
# standing-authorization schema, approval store, or replacement engine is assumed.
# Input: reauthenticated tag/source/target, exact producer attempt, transport and
# release hashes; independently authenticated installed module/bootstrap.
# Future output: the toolkit's verified Deployed/NoOp result. Authorization,
# registration and deployment remain inside the toolkit's supported mechanism.
# The adjacent unpublished draft combines these operations and requires a signed
# receipt from a pinned reusable producer. No such API exists in the build pin.
function Invoke-SsrsToolkitAutomaticDeployment($Release,$Context) {
    throw [NotSupportedException]::new('Ssrs.AutomaticDeployment.ToolkitIntegrationUnavailable')
}

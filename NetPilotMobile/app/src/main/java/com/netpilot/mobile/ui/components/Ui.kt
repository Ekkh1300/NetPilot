package com.netpilot.mobile.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.netpilot.mobile.ui.theme.NpColors

/** Rounded surface used for every block on every page. */
@Composable
fun NpCard(
    modifier: Modifier = Modifier,
    color: Color = NpColors.Card,
    borderColor: Color = NpColors.Stroke,
    onClick: (() -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit
) {
    val shape = RoundedCornerShape(18.dp)
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(shape)
            .background(color)
            .border(1.dp, borderColor, shape)
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(16.dp),
        content = content
    )
}

@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        color = NpColors.Muted,
        fontSize = 12.sp,
        fontWeight = FontWeight.SemiBold,
        letterSpacing = 0.8.sp,
        modifier = modifier.padding(bottom = 8.dp)
    )
}

/** Small labelled figure used across the dashboard, monitor and adapters pages. */
@Composable
fun StatTile(
    label: String,
    value: String,
    sub: String = "",
    accent: Color = NpColors.Accent,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .clip(RoundedCornerShape(14.dp))
            .background(NpColors.CardAlt)
            .padding(horizontal = 12.dp, vertical = 10.dp)
    ) {
        Text(label, color = NpColors.Muted, fontSize = 11.sp)
        Spacer(Modifier.height(3.dp))
        Text(
            value,
            color = accent,
            fontSize = 17.sp,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
        if (sub.isNotEmpty()) {
            Spacer(Modifier.height(2.dp))
            Text(sub, color = NpColors.Muted, fontSize = 10.sp, maxLines = 1)
        }
    }
}

/** Primary filled action. */
@Composable
fun NpButton(
    text: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    accent: Color = NpColors.Accent,
    icon: (@Composable () -> Unit)? = null,
    onClick: () -> Unit
) {
    Row(
        modifier = modifier
            .clip(RoundedCornerShape(12.dp))
            .background(if (enabled) accent else NpColors.Stroke)
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center
    ) {
        if (icon != null) {
            icon()
            Spacer(Modifier.width(8.dp))
        }
        Text(
            text,
            color = if (enabled) Color(0xFF06121B) else NpColors.Muted,
            fontSize = 13.sp,
            fontWeight = FontWeight.Bold
        )
    }
}

/** Secondary outlined action. */
@Composable
fun GhostButton(
    text: String,
    modifier: Modifier = Modifier,
    accent: Color = NpColors.Accent,
    danger: Boolean = false,
    onClick: () -> Unit
) {
    val fg = if (danger) NpColors.Bad else accent
    Row(
        modifier = modifier
            .clip(RoundedCornerShape(12.dp))
            .background(Color.Transparent)
            .border(1.dp, fg.copy(alpha = 0.6f), RoundedCornerShape(12.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center
    ) {
        Text(text, color = fg, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** Selectable pill (provider filter, quick-DNS switcher, sort modes…). */
@Composable
fun NpChip(
    text: String,
    selected: Boolean,
    modifier: Modifier = Modifier,
    accent: Color = NpColors.Accent,
    onClick: () -> Unit
) {
    val shape = RoundedCornerShape(50)
    Row(
        modifier = modifier
            .clip(shape)
            .background(if (selected) accent.copy(alpha = 0.18f) else NpColors.CardAlt)
            .border(
                1.dp,
                if (selected) accent else NpColors.Stroke,
                shape
            )
            .clickable(onClick = onClick)
            .padding(horizontal = 13.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text,
            color = if (selected) accent else NpColors.Muted,
            fontSize = 12.sp,
            fontWeight = if (selected) FontWeight.Bold else FontWeight.Medium
        )
    }
}

@Composable
fun SwitchRow(
    label: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    sub: String = ""
) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(label, color = NpColors.Text, fontSize = 14.sp, fontWeight = FontWeight.Medium)
            if (sub.isNotEmpty()) {
                Spacer(Modifier.height(2.dp))
                Text(sub, color = NpColors.Muted, fontSize = 11.sp)
            }
        }
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            colors = SwitchDefaults.colors(
                checkedTrackColor = NpColors.Accent,
                checkedThumbColor = Color(0xFF06121B),
                uncheckedTrackColor = NpColors.Stroke,
                uncheckedThumbColor = NpColors.Muted
            )
        )
    }
}

@Composable
fun SearchField(
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    modifier: Modifier = Modifier
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier.fillMaxWidth(),
        singleLine = true,
        placeholder = { Text(placeholder, color = NpColors.Muted, fontSize = 13.sp) },
        leadingIcon = {
            Icon(Icons.Default.Search, contentDescription = null, tint = NpColors.Muted)
        },
        trailingIcon = {
            if (value.isNotEmpty()) {
                Icon(
                    Icons.Default.Close,
                    contentDescription = null,
                    tint = NpColors.Muted,
                    modifier = Modifier.clickable { onValueChange("") }
                )
            }
        },
        textStyle = androidx.compose.material3.LocalTextStyle.current.copy(
            color = NpColors.Text,
            fontSize = 13.sp
        ),
        colors = OutlinedTextFieldDefaults.colors(
            focusedBorderColor = NpColors.Accent,
            unfocusedBorderColor = NpColors.Stroke,
            focusedContainerColor = NpColors.Card,
            unfocusedContainerColor = NpColors.Card,
            cursorColor = NpColors.Accent
        ),
        shape = RoundedCornerShape(14.dp)
    )
}

@Composable
fun EmptyState(text: String, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxWidth().padding(vertical = 26.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Icon(
            Icons.AutoMirrored.Filled.List,
            contentDescription = null,
            tint = NpColors.Stroke,
            modifier = Modifier.size(34.dp)
        )
        Spacer(Modifier.height(8.dp))
        Text(text, color = NpColors.Muted, fontSize = 13.sp)
    }
}

/** Custom app bar — keeps full control of the look (Compose's TopAppBar is experimental). */
@Composable
fun NpTopBar(
    title: String,
    onBack: (() -> Unit)? = null,
    actions: (@Composable () -> Unit)? = null
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(NpColors.BgElevated)
            .padding(horizontal = 14.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (onBack != null) {
            Box(
                modifier = Modifier
                    .size(36.dp)
                    .clip(CircleShape)
                    .background(NpColors.CardAlt)
                    .clickable(onClick = onBack),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = "Back",
                    tint = NpColors.Text,
                    modifier = Modifier.size(18.dp)
                )
            }
            Spacer(Modifier.width(12.dp))
        }
        Text(
            title,
            color = NpColors.Text,
            fontSize = 17.sp,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f)
        )
        if (actions != null) actions()
    }
}

/** Vertical space helper used between sections. */
@Composable
fun Gap(height: Int = 12) {
    Spacer(Modifier.height(height.dp))
}

/** Full-page placeholder used for pages that are being assembled. */
@Composable
fun PlaceholderScreen(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = NpColors.Muted)
    }
}

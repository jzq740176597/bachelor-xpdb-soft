#ifndef MY_COMMON_INCLUDED
#define MY_COMMON_INCLUDED

// Your custom atomic workaround for SM 6.5
void CustomInterlockedAddFloat(RWByteAddressBuffer buffer, uint address, float value) {
    uint current_val = buffer.Load(address);
    uint expected_val;
    [allow_uav_condition]
    do {
        expected_val = current_val;
        uint new_val = asuint(asfloat(expected_val) + value);
        buffer.InterlockedCompareExchange(address, expected_val, new_val, current_val);
    } while (current_val != expected_val);
}

#endif